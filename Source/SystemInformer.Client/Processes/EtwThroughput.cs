using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.Etw;
using Windows.Win32.System.Threading;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace SystemInformer.Client.Processes;

/// <summary>Owns a private real-time system logger. No shared kernel session is opened or stopped.</summary>
internal sealed unsafe class EtwThroughput : IDisposable
{
    private static readonly Guid DiskProvider = new("3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c");
    private static readonly Guid TcpProvider = new("9a280ac0-c8e0-11d1-84e2-00c04fb998a2");
    private static readonly Guid UdpProvider = new("bf3a50c5-a9c9-4988-a005-2df0b7c80f80");
    private readonly string _name = $"SystemInformerPlugin-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<(uint Id, ulong Created), Counters> _counters = new();
    private readonly Dictionary<(uint Id, ulong Created), Totals> _previous = new();
    private readonly object _sessionGate = new();
    private Dictionary<uint, ulong> _identities = new();
    private Dictionary<uint, uint> _threads = new();
    private EVENT_TRACE_PROPERTIES* _properties;
    private ulong _session;
    private PROCESSTRACE_HANDLE _consumer;
    private GCHandle _context;
    private Task? _task;
    private ProfilePrivilege? _privilege;
    private long _lastTimestamp, _unattributedDisk;
    private uint _lastLost;
    private int _disposed, _running;
    private string _status = "磁盘/网络尚未采样";
    public string Status => Volatile.Read(ref _status);
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    private sealed class Counters
    {
        public long DiskRead, DiskWrite, Received, Sent;
        public Totals Read() => new(Interlocked.Read(ref DiskRead), Interlocked.Read(ref DiskWrite),
            Interlocked.Read(ref Received), Interlocked.Read(ref Sent));
    }
    private readonly record struct Totals(long Read, long Written, long Received, long Sent);

    public EtwThroughput()
    {
        try
        {
            _privilege = new ProfilePrivilege();
            var size = checked(sizeof(EVENT_TRACE_PROPERTIES) + (_name.Length + 1) * sizeof(char));
            _properties = (EVENT_TRACE_PROPERTIES*)NativeMemory.AllocZeroed((nuint)size);
            _properties->Wnode.BufferSize = (uint)size;
            _properties->Wnode.Guid = Guid.NewGuid();
            _properties->Wnode.ClientContext = 1;
            _properties->Wnode.Flags = 0x00020000; // WNODE_FLAG_TRACED_GUID
            _properties->BufferSize = 64;
            _properties->MinimumBuffers = 4;
            _properties->MaximumBuffers = 32;
            _properties->LogFileMode = 0x02000100; // SYSTEM_LOGGER_MODE | REAL_TIME_MODE
            _properties->FlushTimer = 1;
            _properties->EnableFlags = EVENT_TRACE_FLAG.EVENT_TRACE_FLAG_DISK_IO
                | EVENT_TRACE_FLAG.EVENT_TRACE_FLAG_NETWORK_TCPIP | EVENT_TRACE_FLAG.EVENT_TRACE_FLAG_NO_SYSCONFIG;
            _properties->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);
            _name.AsSpan().CopyTo(new Span<char>((byte*)_properties + sizeof(EVENT_TRACE_PROPERTIES), _name.Length + 1));
            ulong session = default;
            fixed (char* name = _name)
            {
                var error = (uint)PInvoke.StartTrace(&session, new PCWSTR(name), _properties);
                if (error != 0)
                {
                    _status = error == 5 ? "磁盘/网络需要管理员权限" : $"磁盘/网络不可用：{new Win32Exception((int)error).Message}";
                    ReleaseResources();
                    return;
                }
                _session = session;
                _context = GCHandle.Alloc(this);
                var logfile = new EVENT_TRACE_LOGFILEW
                {
                    LoggerName = new PWSTR(name), Context = (void*)GCHandle.ToIntPtr(_context)
                };
                logfile.Anonymous1.ProcessTraceMode = 0x10000100; // REAL_TIME | EVENT_RECORD; timestamps are FILETIME.
                logfile.Anonymous2.EventRecordCallback = &OnEvent;
                _consumer = PInvoke.OpenTrace(&logfile);
            }
            if (_consumer.Value == ulong.MaxValue)
            {
                _status = "磁盘/网络事件流不可读取";
                StopSession();
                ReleaseResources();
                return;
            }
            _status = "磁盘/网络等待速率采样";
            _running = 1;
            _task = Task.Factory.StartNew(Consume, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
        catch (Exception error)
        {
            _status = "磁盘/网络不可用：" + error.Message;
            _running = 0;
            StopSession();
            CloseConsumer();
            ReleaseResources();
        }
    }

    private void Consume()
    {
        try
        {
            var handle = _consumer;
            var error = (uint)PInvoke.ProcessTrace(&handle, 1, null, null);
            if (Volatile.Read(ref _disposed) == 0)
                Volatile.Write(ref _status, $"磁盘/网络事件流已停止（{error}）");
        }
        catch (Exception error)
        {
            if (Volatile.Read(ref _disposed) == 0)
                Volatile.Write(ref _status, "磁盘/网络事件流不可读取：" + error.Message);
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    public void UpdateThreads(Dictionary<uint, uint> owners) => Volatile.Write(ref _threads, owners);
    public void ResetRates() { _lastTimestamp = 0; _previous.Clear(); }

    public IReadOnlyList<ProcessSample> ApplyRates(IReadOnlyList<ProcessSample> samples)
    {
        var identities = samples.Where(sample => !sample.IsSynthetic && sample.Id != 0 && sample.Created != null)
            .ToDictionary(sample => sample.Id, sample => sample.Created!.Value);
        Volatile.Write(ref _identities, identities);
        var now = Stopwatch.GetTimestamp();
        var seconds = _lastTimestamp == 0 ? 0 : (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;
        var available = Volatile.Read(ref _running) != 0 && seconds > 0;
        lock (_sessionGate)
        {
            if (Volatile.Read(ref _disposed) != 0) available = false;
            else if (_session != 0 && _properties != null)
            {
                fixed (char* name = _name)
                {
                    var error = (uint)PInvoke.ControlTrace(_session, new PCWSTR(name), _properties, EVENT_TRACE_CONTROL.EVENT_TRACE_CONTROL_QUERY);
                    if (error != 0) available = false;
                    else
                    {
                        var lost = _properties->EventsLost + _properties->RealTimeBuffersLost;
                        if (lost != _lastLost) available = false;
                        _lastLost = lost;
                    }
                }
            }
        }
        var result = new List<ProcessSample>(samples.Count);
        foreach (var sample in samples)
        {
            double? disk = null, network = null;
            if (!sample.IsSynthetic && sample.Id != 0 && sample.Created is ulong created)
            {
                var key = (sample.Id, created);
                var total = _counters.TryGetValue(key, out var counters) ? counters.Read() : default;
                if (available && _previous.TryGetValue(key, out var before))
                {
                    disk = Math.Max(0, total.Read + total.Written - before.Read - before.Written) / seconds / 1048576d;
                    network = Math.Max(0, total.Received + total.Sent - before.Received - before.Sent) / seconds * 8 / 1_000_000d;
                }
                _previous[key] = total;
            }
            result.Add(sample with { DiskMiBPerSecond = disk, NetworkMbps = network });
        }
        foreach (var key in _previous.Keys.Where(key => !identities.TryGetValue(key.Id, out var created) || created != key.Created).ToArray())
            _previous.Remove(key);
        foreach (var key in _counters.Keys.Where(key => !identities.TryGetValue(key.Id, out var created) || created != key.Created).ToArray())
            _counters.TryRemove(key, out _);
        if (Volatile.Read(ref _running) != 0)
            Volatile.Write(ref _status, seconds == 0 ? "磁盘/网络等待速率采样" : !available ? "磁盘/网络本轮采样不完整"
                : Interlocked.Read(ref _unattributedDisk) != 0 ? "磁盘/网络采样中；部分磁盘事件无法归属" : "磁盘/网络采样中");
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnEvent(EVENT_RECORD* record)
    {
        try
        {
            if (record == null || record->UserContext == null) return;
            if (GCHandle.FromIntPtr((nint)record->UserContext).Target is not EtwThroughput owner || Volatile.Read(ref owner._disposed) != 0) return;
            owner.Record(record);
        }
        catch { /* Native callbacks cannot propagate managed exceptions. */ }
    }

    private void Record(EVENT_RECORD* record)
    {
        var opcode = record->EventHeader.EventDescriptor.Opcode;
        var provider = record->EventHeader.ProviderId;
        var payload = new ReadOnlySpan<byte>(record->UserData, record->UserDataLength);
        uint pid, bytes;
        var disk = provider == DiskProvider;
        if (disk)
        {
            if (opcode is not (10 or 11)) return;
            var offset = (record->EventHeader.Flags & 0x20) != 0 ? 40 : 48;
            if (record->EventHeader.EventDescriptor.Version < 3 || payload.Length < offset + 4) return;
            bytes = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
            var threadId = BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]);
            if (threadId == uint.MaxValue) pid = 4;
            else if (!Volatile.Read(ref _threads).TryGetValue(threadId, out pid))
            {
                using var thread = new Interop.ProcessHandle(PInvoke.OpenThread((THREAD_ACCESS_RIGHTS)0x0800, false, threadId));
                if (thread.IsInvalid) { Interlocked.Increment(ref _unattributedDisk); return; }
                FILETIME created = default, ended = default, kernel = default, user = default;
                if (!PInvoke.GetThreadTimes(thread.Value, &created, &ended, &kernel, &user)
                    || ((ulong)(uint)created.dwHighDateTime << 32 | (uint)created.dwLowDateTime) > (ulong)record->EventHeader.TimeStamp)
                { Interlocked.Increment(ref _unattributedDisk); return; }
                pid = PInvoke.GetProcessIdOfThread(thread.Value);
            }
        }
        else
        {
            if ((provider != TcpProvider && provider != UdpProvider) || opcode is not (10 or 11 or 26 or 27) || payload.Length < 8) return;
            pid = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            bytes = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        }
        if (!Volatile.Read(ref _identities).TryGetValue(pid, out var identity) || (ulong)record->EventHeader.TimeStamp < identity) return;
        var counter = _counters.GetOrAdd((pid, identity), _ => new Counters());
        if (disk)
        {
            if (opcode == 10) Interlocked.Add(ref counter.DiskRead, bytes);
            else Interlocked.Add(ref counter.DiskWrite, bytes);
        }
        else if (opcode is 10 or 26) Interlocked.Add(ref counter.Sent, bytes);
        else Interlocked.Add(ref counter.Received, bytes);
    }

    private void StopSession()
    {
        lock (_sessionGate)
        {
            if (_session == 0 || _properties == null) return;
            fixed (char* name = _name) PInvoke.ControlTrace(_session, new PCWSTR(name), _properties, EVENT_TRACE_CONTROL.EVENT_TRACE_CONTROL_STOP);
            _session = default;
        }
    }
    private void CloseConsumer()
    {
        if (_consumer.Value == 0 || _consumer.Value == ulong.MaxValue) return;
        PInvoke.CloseTrace(_consumer);
        _consumer = default;
    }
    private void ReleaseResources()
    {
        lock (_sessionGate)
        {
            if (_context.IsAllocated) _context.Free();
            if (_properties != null) { NativeMemory.Free(_properties); _properties = null; }
            _privilege?.Dispose();
            _privilege = null;
        }
    }
    private void OnProcessExit(object? sender, EventArgs args) => Dispose();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        Volatile.Write(ref _running, 0);
        StopSession();
        CloseConsumer();
        if (_task == null || _task.Wait(TimeSpan.FromSeconds(5))) ReleaseResources();
        else _ = _task.ContinueWith(_ => ReleaseResources(), TaskScheduler.Default);
    }
}

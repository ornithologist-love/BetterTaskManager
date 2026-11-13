using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.System.ProcessStatus;
using Windows.Win32.System.SystemInformation;
using Windows.Win32.System.WindowsProgramming;
using Windows.Wdk.System.SystemInformation;

namespace SystemInformer.Client.Performance;

internal sealed record CpuTimes(double? Kernel, double? User, IReadOnlyList<(double? Kernel, double? User)> Cores);
internal sealed record SystemMetricsFrame(CpuTimes Cpu, double? PhysicalTotal, double? PhysicalAvailable,
    double? Commit, double? CommitLimit, double? CommitPeak, double? Cache, double? PagedPool, double? NonpagedPool,
    double? Modified, double? Standby, uint? Processes, uint? Threads, uint? Handles,
    double? IoRead, double? IoWrite, double? IoOther);

// Reads public SDK structures and the stable NT counter prefixes, independently of process filtering.
internal sealed unsafe class SystemMetrics : IDisposable
{
    private readonly nint _ntdll = NativeLibrary.Load("ntdll.dll");
    private readonly delegate* unmanaged[Stdcall]<uint, void*, uint, void*, uint, uint*, int> _queryGroup;
    private Dictionary<(ushort Group, int Cpu), (long Idle, long Kernel, long User)> _cpu = new();
    private (ulong Read, ulong Write, ulong Other, long Time)? _io;
    public string CpuName { get; }
    public IReadOnlyList<Metric> CpuHardware { get; }
    public IReadOnlyList<Metric> MemoryHardware { get; }
    public double? InstalledMemory { get; }

    public SystemMetrics()
    {
        if (NativeLibrary.TryGetExport(_ntdll, "NtQuerySystemInformationEx", out var address))
            _queryGroup = (delegate* unmanaged[Stdcall]<uint, void*, uint, void*, uint, uint*, int>)address;
        CpuName = "CPU";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            CpuName = key?.GetValue("ProcessorNameString") as string ?? "CPU";
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        CpuHardware = ReadCpuHardware();
        MemoryHardware = ReadMemoryHardware(out var installedMemory);
        InstalledMemory = installedMemory;
    }

    public void Reset() { _cpu.Clear(); _io = null; }

    public SystemMetricsFrame Capture(double? cycleTotal, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var cpu = ReadCpu(cycleTotal, token);
        var perf = new PERFORMANCE_INFORMATION { cb = (uint)sizeof(PERFORMANCE_INFORMATION) };
        var readable = PInvoke.K32GetPerformanceInfo(&perf, perf.cb);
        var pageSize = (double)perf.PageSize;
        double? Pages(nuint pages) => readable ? (double)pages * pageSize : null;
        double? modified = null, standby = null;
        byte* list = stackalloc byte[256];
        new Span<byte>(list, 256).Clear();
        uint returned = 0;
        if (Windows.Wdk.PInvoke.NtQuerySystemInformation((SYSTEM_INFORMATION_CLASS)80, list, 256, &returned).Value >= 0
            && returned >= 13 * sizeof(ulong) && readable)
        {
            // SystemMemoryListInformation on Windows x64: zero/free/modified/no-write/bad, then eight standby lists.
            var pages = (ulong*)list;
            modified = pages[2] * (double)perf.PageSize;
            double standbyPages = 0;
            for (var i = 5; i < 13; i++) standbyPages += pages[i];
            standby = standbyPages * (double)perf.PageSize;
        }
        var io = ReadIo();
        return new(cpu, Pages(perf.PhysicalTotal), Pages(perf.PhysicalAvailable), Pages(perf.CommitTotal),
            Pages(perf.CommitLimit), Pages(perf.CommitPeak), Pages(perf.SystemCache), Pages(perf.KernelPaged),
            Pages(perf.KernelNonpaged), modified, standby, readable ? perf.ProcessCount : null,
            readable ? perf.ThreadCount : null, readable ? perf.HandleCount : null, io.Read, io.Write, io.Other);
    }

    private CpuTimes ReadCpu(double? cycleTotal, CancellationToken token)
    {
        var current = new Dictionary<(ushort, int), (long, long, long)>();
        var cores = new List<(double? Kernel, double? User)>();
        double kernel = 0, user = 0;
        var complete = _queryGroup != null;
        var groups = PInvoke.GetActiveProcessorGroupCount();
        SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION* values = stackalloc SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION[64];
        for (ushort group = 0; group < groups; group++)
        {
            token.ThrowIfCancellationRequested();
            var count = PInvoke.GetActiveProcessorCount(group);
            uint bytes = count * (uint)sizeof(SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION), returned = 0;
            var valid = _queryGroup != null && count is > 0 and <= 64
                && _queryGroup(8, &group, sizeof(ushort), values, bytes, &returned) >= 0 && returned == bytes;
            if (!valid) { complete = false; for (var i = 0; i < count; i++) cores.Add((null, null)); continue; }
            for (var i = 0; i < count; i++)
            {
                var now = (Idle: values[i].IdleTime, Kernel: values[i].KernelTime, User: values[i].UserTime);
                current[(group, i)] = now;
                if (!_cpu.TryGetValue((group, i), out var before)
                    || now.Idle < before.Idle || now.Kernel < before.Kernel || now.User < before.User)
                { cores.Add((null, null)); complete = false; continue; }
                var idle = (double)(now.Idle - before.Idle);
                var busyKernel = (double)(now.Kernel - before.Kernel) - idle;
                var busyUser = (double)(now.User - before.User);
                var total = busyKernel + busyUser + idle;
                if (busyKernel < 0 || total <= 0) { cores.Add((null, null)); complete = false; continue; }
                kernel += busyKernel; user += busyUser;
                cores.Add((100 * busyKernel / total, 100 * busyUser / total));
            }
        }
        _cpu = current;
        if (!complete || cycleTotal == null || groups == 0) return new(null, null, cores);
        // Upstream splits its cycle-based total using the kernel/user busy-time proportions.
        var fraction = kernel + user > 0 ? kernel / (kernel + user) : 0.5;
        return new(cycleTotal * fraction, cycleTotal * (1 - fraction), cores);
    }

    private (double? Read, double? Write, double? Other) ReadIo()
    {
        byte* buffer = stackalloc byte[1024];
        new Span<byte>(buffer, 1024).Clear();
        uint returned = 0;
        if (Windows.Wdk.PInvoke.NtQuerySystemInformation((SYSTEM_INFORMATION_CLASS)2, buffer, 1024, &returned).Value < 0
            || returned < 32) { _io = null; return (null, null, null); }
        // SYSTEM_PERFORMANCE_INFORMATION prefix: idle time, then three cumulative I/O byte counts.
        var counts = (ulong*)buffer;
        var now = (Read: counts[1], Write: counts[2], Other: counts[3], Time: System.Diagnostics.Stopwatch.GetTimestamp());
        var previous = _io; _io = now;
        if (previous is not { } before || now.Read < before.Read || now.Write < before.Write || now.Other < before.Other)
            return (null, null, null);
        var seconds = (now.Time - before.Time) / (double)System.Diagnostics.Stopwatch.Frequency;
        return seconds > 0 ? ((now.Read - before.Read) / seconds, (now.Write - before.Write) / seconds,
            (now.Other - before.Other) / seconds) : (null, null, null);
    }

    private static IReadOnlyList<Metric> ReadCpuHardware()
    {
        var metrics = new List<Metric>
        {
            new("逻辑处理器", PInvoke.GetActiveProcessorCount(ushort.MaxValue).ToString()),
            new("处理器组", PInvoke.GetActiveProcessorGroupCount().ToString())
        };
        uint bytes = 0;
        PInvoke.GetLogicalProcessorInformationEx((LOGICAL_PROCESSOR_RELATIONSHIP)65535, null, &bytes);
        if (bytes is 0 or > 1_048_576) return metrics;
        var buffer = (byte*)NativeMemory.Alloc(bytes);
        try
        {
            var capacity = bytes;
            if (!PInvoke.GetLogicalProcessorInformationEx((LOGICAL_PROCESSOR_RELATIONSHIP)65535,
                (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)buffer, &bytes) || bytes > capacity) return metrics;
            var span = new ReadOnlySpan<byte>(buffer, checked((int)bytes));
            var cores = 0; var sockets = 0;
            var caches = new Dictionary<byte, ulong>();
            while (span.Length >= 8)
            {
                var type = BinaryPrimitives.ReadUInt32LittleEndian(span);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                if (size < 8 || size > span.Length) break;
                if (type == 0) cores++;
                if (type == 3) sockets++;
                if (type == 2 && size >= 16)
                {
                    var level = span[8];
                    caches[level] = caches.GetValueOrDefault(level) + BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                }
                span = span[(int)size..];
            }
            metrics.Add(new("内核", cores.ToString())); metrics.Add(new("插槽", sockets.ToString()));
            foreach (var pair in caches.OrderBy(pair => pair.Key)) metrics.Add(new($"L{pair.Key} 缓存", ResourceFormat.Bytes(pair.Value)));
        }
        finally { NativeMemory.Free(buffer); }
        return metrics;
    }

    private static IReadOnlyList<Metric> ReadMemoryHardware(out double? installedMemory)
    {
        var metrics = new List<Metric>();
        ulong installed = 0;
        installedMemory = PInvoke.GetPhysicallyInstalledSystemMemory(&installed) ? installed * 1024d : null;
        if (installedMemory.HasValue) metrics.Add(new("已安装", ResourceFormat.Bytes(installedMemory)));
        var provider = (FIRMWARE_TABLE_PROVIDER)0x52534D42; // RSMB
        var bytes = PInvoke.GetSystemFirmwareTable(provider, 0, null, 0);
        if (bytes is < 8 or > 1_048_576) return metrics;
        var buffer = (byte*)NativeMemory.Alloc(bytes);
        try
        {
            var returned = PInvoke.GetSystemFirmwareTable(provider, 0, buffer, bytes);
            if (returned < 8 || returned > bytes) return metrics;
            var raw = new ReadOnlySpan<byte>(buffer, checked((int)returned));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
            if (length > raw.Length - 8) return metrics;
            var table = raw.Slice(8, (int)length);
            var slots = 0; var used = 0;
            var speeds = new HashSet<int>(); var types = new HashSet<string>(); var forms = new HashSet<string>();
            while (table.Length >= 4)
            {
                var size = table[1];
                if (size < 4 || size > table.Length) break;
                if (table[0] == 17 && size >= 0x15)
                {
                    slots++;
                    if (BinaryPrimitives.ReadUInt16LittleEndian(table[0xC..]) != 0)
                    {
                        used++;
                        if (table[0xE] is 9 or 13) forms.Add(table[0xE] == 9 ? "DIMM" : "SODIMM");
                        var type = table[0x12] switch { 0x18 => "DDR3", 0x1A => "DDR4", 0x22 => "DDR5", 0x1E => "LPDDR4", 0x23 => "LPDDR5", _ => "" };
                        if (type.Length != 0) types.Add(type);
                        var speed = size >= 0x22 ? BinaryPrimitives.ReadUInt16LittleEndian(table[0x20..]) : (ushort)0;
                        if (speed is 0 or 65535 && size >= 0x17) speed = BinaryPrimitives.ReadUInt16LittleEndian(table[0x15..]);
                        if (speed is > 0 and < 65535) speeds.Add(speed);
                    }
                }
                if (table[0] == 127) break;
                var end = size;
                while (end + 1 < table.Length && (table[end] != 0 || table[end + 1] != 0)) end++;
                if (end + 1 >= table.Length) break;
                table = table[(end + 2)..];
            }
            if (slots > 0) metrics.Add(new("已使用的插槽", $"{used}/{slots}"));
            if (types.Count > 0) metrics.Add(new("类型", string.Join(" / ", types.Order())));
            if (forms.Count > 0) metrics.Add(new("外形规格", string.Join(" / ", forms.Order())));
            if (speeds.Count > 0) metrics.Add(new("速度", string.Join(" / ", speeds.Order()) + " MT/s"));
        }
        finally { NativeMemory.Free(buffer); }
        return metrics;
    }

    public void Dispose() => NativeLibrary.Free(_ntdll);
}

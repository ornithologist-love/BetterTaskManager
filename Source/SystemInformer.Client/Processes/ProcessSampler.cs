using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.ProcessStatus;
using Windows.Win32.System.Threading;
using SystemInformer.Client.Interop;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace SystemInformer.Client.Processes;

public sealed record ProcessSample(uint Id, ulong? Created, string Name, string Path,
    double? CpuPercent, double? WorkingSetMiB, double? PrivateCommitMiB, string Availability,
    uint ParentId = 0, ProcessIcon? Icon = null, string WindowRole = "未知", string ExecutionState = "",
    double? DiskMiBPerSecond = null, double? NetworkMbps = null,
    string Description = "", string ProductName = "", string WindowTitle = "", bool IsSynthetic = false,
    double? PrivateWorkingSetMiB = null);

/// <summary>Single-reader sampler. Retained handles pin identity across PID reuse.</summary>
public sealed unsafe class ProcessSampler : IDisposable
{
    private readonly Dictionary<uint, ObservedProcess> _processes = new();
    private readonly ProcessIconCache _icons = new();
    private readonly ProcessWindows _windows = new();
    private readonly ProcessDescriptions _descriptions = new();
    private EtwThroughput? _throughput;
    private SystemResourceSampler? _system;
    private ProcessCpuSampler? _cpu;
    public SystemResourceSample SystemResources { get; private set; } = new();
    public string CpuStatus { get; private set; } = "CPU 周期尚未采样";
    public Action<string>? CpuDiagnostics { get; set; }
    public string ThroughputStatus => _throughput?.Status ?? "磁盘/网络尚未采样";

    public IReadOnlyList<ProcessSample> Capture(CancellationToken token)
    {
        _throughput ??= new EtwThroughput();
        _cpu ??= new ProcessCpuSampler(CpuDiagnostics);
        var cpuFrame = _cpu.Capture(ReadFinalCycles, token);
        CpuStatus = cpuFrame.Status;
        _windows.Capture();
        var collectThreads = _throughput.IsRunning;
        var result = new List<ProcessSample>();
        var threadOwners = new Dictionary<uint, uint>();
        if (collectThreads)
        {
            using var snapshot = new ProcessHandle(PInvoke.CreateToolhelp32Snapshot((CREATE_TOOLHELP_SNAPSHOT_FLAGS)4, 0));
            var thread = new THREADENTRY32 { dwSize = (uint)sizeof(THREADENTRY32) };
            if (!snapshot.IsInvalid && PInvoke.Thread32First(snapshot.Value, &thread))
            {
                do { token.ThrowIfCancellationRequested(); threadOwners[thread.th32ThreadID] = thread.th32OwnerProcessID; }
                while (PInvoke.Thread32Next(snapshot.Value, &thread));
            }
        }
        _throughput.UpdateThreads(threadOwners);
        var alive = new HashSet<uint>();
        foreach (var entry in cpuFrame.Processes)
        {
            token.ThrowIfCancellationRequested();
            alive.Add(entry.Id);
            if (_processes.TryGetValue(entry.Id, out var observed)
                && (observed.Created != entry.Created
                    || observed.Handle != null && PInvoke.WaitForSingleObject(observed.Handle.Value, 0) == 0))
            {
                observed.Dispose();
                _processes.Remove(entry.Id);
                observed = null;
            }
            if (observed == null)
            {
                observed = new ObservedProcess(entry.Id, entry.Created, entry.Name);
                _processes.Add(entry.Id, observed);
            }
            observed.Name = entry.Name;
            observed.ParentId = entry.ParentId;
            // A denied handle is retried at a low frequency; other columns remain visible.
            if (observed.Handle == null && DateTime.UtcNow >= observed.RetryAt)
                observed.TryOpen();
            cpuFrame.Percentages.TryGetValue(entry.Id, out var cpu);
            var sample = observed.Read(cpu, _icons, _windows);
            if (sample != null)
            {
                var description = _descriptions.Get(sample.Path);
                result.Add(sample with { Description = description.Description, ProductName = description.ProductName,
                    WindowTitle = _windows.Titles(sample.Id) });
            }
        }
        foreach (var id in _processes.Keys.Where(id => !alive.Contains(id)).ToArray())
        {
            _processes[id].Dispose();
            _processes.Remove(id);
        }
        _icons.Trim(result.Select(sample => sample.Path));
        _descriptions.Trim(result.Select(sample => sample.Path));
        _system ??= new SystemResourceSampler();
        SystemResources = _system.Capture() with { CpuPercent = cpuFrame.TotalPercent };
        result.Add(new(uint.MaxValue, 0, "中断与 DPC", "", cpuFrame.InterruptPercent, null, null,
            cpuFrame.InterruptPercent == null ? CpuStatus : "系统中断和延迟过程调用，不对应普通进程",
            WindowRole: "后台", IsSynthetic: true));
        return _throughput.ApplyRates(result);
    }

    private ulong? ReadFinalCycles(uint id, ulong created)
    {
        // Called before removed/reused identities release their retained handles.
        if (!_processes.TryGetValue(id, out var process) || process.Created != created || process.Handle == null
            || PInvoke.WaitForSingleObject(process.Handle.Value, 0) != 0) return null;
        ulong cycles = 0;
        return PInvoke.QueryProcessCycleTime(process.Handle.Value, &cycles) ? cycles : null;
    }

    public void ResetCpuBaselines()
    {
        _cpu?.Reset();
        _throughput?.ResetRates();
        _system?.Reset();
    }

    public void PauseMonitoring()
    {
        _throughput?.Dispose(); _throughput = null;
        _system?.Dispose(); _system = null;
        _cpu?.Dispose(); _cpu = null;
        SystemResources = new();
        CpuStatus = "CPU 周期尚未采样";
    }

    public void Dispose()
    {
        PauseMonitoring();
        foreach (var process in _processes.Values) process.Dispose();
        _processes.Clear();
        _icons.Clear();
        _descriptions.Clear();
    }

    private sealed class ObservedProcess(uint id, ulong created, string name) : IDisposable
    {
        public string Name = name;
        public uint ParentId;
        public ProcessHandle? Handle;
        public DateTime RetryAt;
        public ulong Created { get; } = created;
        private string _path = "";

        public void TryOpen()
        {
            RetryAt = DateTime.UtcNow.AddSeconds(10);
            if (id == 0) return;
            var handle = new ProcessHandle(PInvoke.OpenProcess((PROCESS_ACCESS_RIGHTS)(0x1000 | 0x100000), false, id));
            if (handle.IsInvalid) { handle.Dispose(); return; }
            FILETIME actualCreated = default, exited = default, kernel = default, user = default;
            if (!PInvoke.GetProcessTimes(handle.Value, &actualCreated, &exited, &kernel, &user)
                || Ticks(actualCreated) != Created) { handle.Dispose(); return; }
            Handle = handle;
            char* path = stackalloc char[32768];
            uint length = 32768;
            if (PInvoke.QueryFullProcessImageName(handle.Value, 0, new PWSTR(path), &length))
                _path = new string(path, 0, checked((int)length));
        }

        public ProcessSample? Read(double? cpu, ProcessIconCache icons, ProcessWindows windows)
        {
            if (Handle == null)
                return new(id, Created, Name, _path, cpu, null, null,
                    id == 0 ? "空闲周期用于整机分母" : "路径或内存不可读取", ParentId, icons.Get(_path),
                    windows.Role(id), windows.IsHung(id) ? "无响应" : "—");
            if (PInvoke.WaitForSingleObject(Handle.Value, 0) == 0) return null;
            // The sentinel also detects older Windows versions that accept the extended buffer
            // but only fill the EX prefix. Never substitute total working set for private memory.
            var memory = new PROCESS_MEMORY_COUNTERS_EX2
            {
                cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS_EX2), PrivateWorkingSetSize = nuint.MaxValue
            };
            var hasMemory = PInvoke.K32GetProcessMemoryInfo(Handle.Value, (PROCESS_MEMORY_COUNTERS*)&memory, memory.cb);
            double? privateWorkingSet = hasMemory && memory.PrivateWorkingSetSize <= memory.WorkingSetSize
                ? (double)memory.PrivateWorkingSetSize / 1048576 : null;
            if (!hasMemory)
            {
                memory.cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS_EX);
                hasMemory = PInvoke.K32GetProcessMemoryInfo(Handle.Value, (PROCESS_MEMORY_COUNTERS*)&memory, memory.cb);
            }
            if (PInvoke.WaitForSingleObject(Handle.Value, 0) == 0) return null;
            var power = new PROCESS_POWER_THROTTLING_STATE { Version = 1 };
            var powerAvailable = PInvoke.GetProcessInformation(Handle.Value, PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                &power, (uint)sizeof(PROCESS_POWER_THROTTLING_STATE));
            var eco = powerAvailable && (power.ControlMask & power.StateMask & 1) != 0;
            var state = windows.IsHung(id) ? (eco ? "无响应 · 节能" : "无响应")
                : eco ? "节能策略" : powerAvailable ? "" : "—";
            return new(id, Created, Name, _path, cpu,
                hasMemory ? (double)memory.WorkingSetSize / 1048576 : null,
                hasMemory ? (double)memory.PrivateUsage / 1048576 : null,
                !hasMemory ? "内存数据不可读取" : privateWorkingSet == null ? "私有工作集不可读取；可查看总工作集和私有提交"
                    : cpu == null ? "等待 CPU 周期采样" : "",
                ParentId, icons.Get(_path), windows.Role(id), state, PrivateWorkingSetMiB: privateWorkingSet);
        }

        private static ulong Ticks(FILETIME value) => ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;
        public void Dispose() => Handle?.Dispose();
    }
}

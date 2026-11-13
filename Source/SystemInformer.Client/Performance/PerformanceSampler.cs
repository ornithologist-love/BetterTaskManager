using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using SystemInformer.Client.Processes;

namespace SystemInformer.Client.Performance;

// Only the sampling worker owns this state. UI snapshots contain immutable copies of bounded histories.
internal sealed class PerformanceSampler : IDisposable
{
    private readonly SystemMetrics _system = new();
    private readonly GraphicsDevices _graphics = new();
    private PerformanceCounters? _counters;
    private readonly Dictionary<string, Queue<HistoryPoint>> _history = new();
    private readonly Dictionary<string, (long Received, long Sent, long Time, long Speed)> _network = new();
    private readonly Dictionary<string, DateTimeOffset> _seen = new();
    private const int Capacity = 301;
    private static readonly Regex EngineName = new(@"luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)_eng_(\d+)_engtype_(.+?)(?:#\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MemoryName = new(@"luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)(?:#\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public PerformanceSnapshot Capture(IReadOnlyList<ProcessSample> processes, SystemResourceSample totals,
        string cpuStatus, CancellationToken token)
    {
        var now = DateTimeOffset.Now;
        _counters ??= new();
        _counters.Capture();
        var frame = _system.Capture(totals.CpuPercent, token);
        var sections = new List<PerformanceSection>();
        var highest = processes.Where(process => !process.IsSynthetic && process.Id != 0 && process.CpuPercent.HasValue)
            .MaxBy(process => process.CpuPercent);
        var note = highest == null ? null : $"最高 CPU：{(highest.Description.Length == 0 ? highest.Name : highest.Description)}（PID {highest.Id}） {highest.CpuPercent:N1}%";
        var cpu = Series("cpu", "CPU", "内核态", "用户态", "%", 100, true, now, frame.Cpu.Kernel, frame.Cpu.User, note);
        var coreSeries = frame.Cpu.Cores.Select((core, i) => Series($"cpu/{i}", $"逻辑 CPU {i}", "内核态", "用户态",
            "%", 100, true, now, core.Kernel, core.User)).ToArray();
        var cpuMetrics = new List<Metric>
        {
            new("占用", ResourceFormat.Percent(totals.CpuPercent)),
            new("频率", _counters.Value(PerformanceCounters.Paths[0]) is double mhz ? $"{mhz / 1000:N2} GHz" : "—"),
            new("进程", frame.Processes?.ToString("N0") ?? "—"), new("线程", frame.Threads?.ToString("N0") ?? "—"),
            new("句柄", frame.Handles?.ToString("N0") ?? "—"),
            new("运行时间", TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\:hh\:mm\:ss")),
            new("上下文切换", ResourceFormat.Number(_counters.Value(PerformanceCounters.Paths[1]), "/s")),
            new("系统调用", ResourceFormat.Number(_counters.Value(PerformanceCounters.Paths[2]), "/s")),
            new("中断", ResourceFormat.Number(_counters.Value(PerformanceCounters.Paths[3]), "/s")),
            new("DPC", ResourceFormat.Number(_counters.Value(PerformanceCounters.Paths[4]), "/s"))
        };
        cpuMetrics.AddRange(_system.CpuHardware);
        sections.Add(new("cpu", ResourceKind.Cpu, "CPU", _system.CpuName.Trim(), ResourceFormat.Percent(totals.CpuPercent),
            cpuStatus + "；主图下层为内核态，上层为用户态；各逻辑 CPU 曲线使用运行时间占比。", cpu, cpuMetrics, coreSeries));

        var used = Subtract(frame.PhysicalTotal, frame.PhysicalAvailable);
        var memory = Series("memory", "物理内存使用量", "使用中", null, "B", frame.PhysicalTotal, false, now, used);
        var committed = Series("memory/commit", "已提交", "提交量", null, "B", frame.CommitLimit, false, now, frame.Commit);
        var memoryMetrics = new List<Metric>
        {
            new("使用中", ResourceFormat.Bytes(used)), new("可用", ResourceFormat.Bytes(frame.PhysicalAvailable)),
            new("已提交 / 上限", $"{ResourceFormat.Bytes(frame.Commit)} / {ResourceFormat.Bytes(frame.CommitLimit)}"),
            new("提交峰值", ResourceFormat.Bytes(frame.CommitPeak)), new("系统缓存", ResourceFormat.Bytes(frame.Cache)),
            new("分页池", ResourceFormat.Bytes(frame.PagedPool)), new("非分页池", ResourceFormat.Bytes(frame.NonpagedPool)),
            new("已修改", ResourceFormat.Bytes(frame.Modified)), new("待机", ResourceFormat.Bytes(frame.Standby)),
            new("已缓存", ResourceFormat.Bytes(Add(frame.Standby, frame.Modified))),
            new("为硬件保留", ResourceFormat.Bytes(Subtract(_system.InstalledMemory, frame.PhysicalTotal)))
        };
        memoryMetrics.AddRange(_system.MemoryHardware);
        List<MemoryPart>? composition = null;
        if (used is double inUse && frame.PhysicalAvailable is double available)
        {
            composition = new();
            if (frame.Modified is double changed && frame.Standby is double standby)
            {
                changed = Math.Min(changed, inUse); standby = Math.Min(standby, available);
                composition.AddRange([new("使用中", inUse - changed), new("已修改", changed), new("待机", standby), new("空闲", available - standby)]);
            }
            else composition.AddRange([new("使用中", inUse), new("可用", available)]);
        }
        sections.Add(new("memory", ResourceKind.Memory, "内存", ResourceFormat.Bytes(frame.PhysicalTotal),
            $"{ResourceFormat.Bytes(used)} / {ResourceFormat.Bytes(frame.PhysicalTotal)}", "物理内存与提交量分别记录；组合图按内存页状态显示。",
            memory, memoryMetrics, [committed], composition));

        var readOther = Add(frame.IoRead, frame.IoOther);
        var io = Series("io", "系统 I/O", "读取及其他", "写入", "B/s", null, true, now, readOther, frame.IoWrite);
        sections.Add(new("io", ResourceKind.Io, "I/O", "系统传输", $"R+O {ResourceFormat.Rate(readOther)}\nW {ResourceFormat.Rate(frame.IoWrite)}",
            "包含文件、设备等系统 I/O，与物理磁盘读写分别统计。", io,
            [new("读取", ResourceFormat.Rate(frame.IoRead)), new("写入", ResourceFormat.Rate(frame.IoWrite)), new("其他", ResourceFormat.Rate(frame.IoOther))], []));

        AddDisks(sections, now);
        AddNetwork(sections, now);
        AddGraphics(sections, now, token);
        foreach (var key in _seen.Where(pair => now - pair.Value > TimeSpan.FromMinutes(6)).Select(pair => pair.Key).ToArray())
        { _seen.Remove(key); _history.Remove(key); }
        return new(now, sections);
    }

    private void AddDisks(List<PerformanceSection> sections, DateTimeOffset now)
    {
        var idle = _counters!.Values(PerformanceCounters.Paths[5]);
        var read = _counters.Values(PerformanceCounters.Paths[6]);
        var write = _counters.Values(PerformanceCounters.Paths[7]);
        var latency = _counters.Values(PerformanceCounters.Paths[8]);
        var queue = _counters.Values(PerformanceCounters.Paths[9]);
        var devices = idle.Keys.Concat(read.Keys).Concat(write.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => name != "_Total").OrderBy(name => DiskIndex(name)).ThenBy(name => name).ToArray();
        foreach (var instance in devices)
        {
            var key = "disk/" + instance;
            var busy = idle.GetValueOrDefault(instance) is double value ? Math.Clamp(100 - value, 0, 100) : (double?)null;
            var readRate = read.GetValueOrDefault(instance); var writeRate = write.GetValueOrDefault(instance);
            var main = Series(key, "活动时间", "活动时间", null, "%", 100, false, now, busy);
            var transfer = Series(key + "/transfer", "磁盘传输速率", "读取", "写入", "B/s", null, true, now, readRate, writeRate);
            var split = instance.IndexOf(' ');
            var title = split < 0 ? $"磁盘 {instance}" : $"磁盘 {instance[..split]}（{instance[(split + 1)..]}）";
            sections.Add(new(key, ResourceKind.Disk, title, "物理磁盘", ResourceFormat.Percent(busy),
                busy == null ? "磁盘计数器尚未就绪或不可读取。" : "活动时间按该物理磁盘的空闲时间计算。", main,
                [new("活动时间", ResourceFormat.Percent(busy)), new("读取", ResourceFormat.Rate(readRate)), new("写入", ResourceFormat.Rate(writeRate)),
                    new("平均响应时间", latency.GetValueOrDefault(instance) is double seconds ? $"{seconds * 1000:N2} ms" : "—"),
                    new("当前队列", ResourceFormat.Number(queue.GetValueOrDefault(instance)))], [transfer]));
        }
        if (devices.Length == 0) sections.Add(Unavailable("disk", ResourceKind.Disk, "磁盘", "物理磁盘计数器不可读取或尚未就绪", now));
    }

    private void AddNetwork(List<PerformanceSection> sections, DateTimeOffset now)
    {
        var alive = new HashSet<string>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().OrderBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                alive.Add(adapter.Id);
                var key = "network/" + adapter.Id;
                double? received = null, sent = null;
                var status = adapter.OperationalStatus == OperationalStatus.Up ? "等待速率采样" : "未连接";
                var metrics = new List<Metric> { new("适配器", adapter.Description), new("状态", adapter.OperationalStatus.ToString()),
                    new("链路速率", adapter.Speed > 0 ? $"{adapter.Speed / 1_000_000d:N0} Mbps" : "—") };
                try
                {
                    var bytes = adapter.GetIPStatistics(); var time = Stopwatch.GetTimestamp();
                    if (adapter.OperationalStatus == OperationalStatus.Up && _network.TryGetValue(adapter.Id, out var before)
                        && before.Speed == adapter.Speed && bytes.BytesReceived >= before.Received && bytes.BytesSent >= before.Sent)
                    {
                        var seconds = (time - before.Time) / (double)Stopwatch.Frequency;
                        if (seconds > 0) { received = (bytes.BytesReceived - before.Received) / seconds; sent = (bytes.BytesSent - before.Sent) / seconds; status = "接收与发送速率"; }
                    }
                    if (adapter.OperationalStatus == OperationalStatus.Up) _network[adapter.Id] = (bytes.BytesReceived, bytes.BytesSent, time, adapter.Speed);
                    else _network.Remove(adapter.Id);
                    var addresses = adapter.GetIPProperties().UnicastAddresses.Select(address => address.Address.ToString());
                    metrics.Add(new("IP 地址", string.Join(" / ", addresses)));
                }
                catch (NetworkInformationException) { _network.Remove(adapter.Id); status = "网卡数据不可读取"; }
                metrics.InsertRange(0, [new("接收", ResourceFormat.Rate(received)), new("发送", ResourceFormat.Rate(sent))]);
                var main = Series(key, "网络流量", "接收", "发送", "B/s", null, true, now, received, sent);
                sections.Add(new(key, ResourceKind.Network, adapter.Name, adapter.Description,
                    $"R {ResourceFormat.Rate(received)}\nS {ResourceFormat.Rate(sent)}", status, main, metrics, []));
            }
        }
        catch (NetworkInformationException) { _network.Clear(); }
        foreach (var key in _network.Keys.Where(key => !alive.Contains(key)).ToArray()) _network.Remove(key);
        if (alive.Count == 0) sections.Add(Unavailable("network", ResourceKind.Network, "网络", "未发现可读取的网卡", now));
    }

    private void AddGraphics(List<PerformanceSection> sections, DateTimeOffset now, CancellationToken token)
    {
        var engines = _counters!.Values(PerformanceCounters.Paths[10]);
        var dedicated = _counters.Values(PerformanceCounters.Paths[11]);
        var shared = _counters.Values(PerformanceCounters.Paths[12]);
        var devices = _graphics.Capture();
        var gpuIndex = 0; var npuIndex = 0;
        foreach (var device in devices)
        {
            token.ThrowIfCancellationRequested();
            var key = (device.Kind == ResourceKind.Npu ? "npu/" : "gpu/") + device.Luid;
            var grouped = new Dictionary<string, (string Label, double Sum, bool Valid)>();
            foreach (var pair in engines)
            {
                var match = EngineName.Match(pair.Key);
                if (!match.Success || !string.Equals(match.Groups[1].Value, device.Luid, StringComparison.OrdinalIgnoreCase)) continue;
                var engineKey = match.Groups[2].Value + "/" + match.Groups[3].Value;
                var current = grouped.GetValueOrDefault(engineKey, (Label: match.Groups[4].Value, Sum: 0d, Valid: true));
                grouped[engineKey] = (current.Label, current.Sum + (pair.Value ?? 0), current.Valid && pair.Value.HasValue);
            }
            var extra = new List<PerformanceSeries>();
            double? usage = null;
            foreach (var pair in grouped.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                double? value = pair.Value.Valid ? Math.Clamp(pair.Value.Sum, 0, 100) : null;
                if (value.HasValue) usage = Math.Max(usage ?? 0, value.Value);
                extra.Add(Series(key + "/engine/" + pair.Key, pair.Value.Label + "（" + pair.Key + "）", pair.Value.Label,
                    null, "%", 100, false, now, value));
            }
            // A partial set of engine counters cannot establish an honest whole-device maximum.
            if (grouped.Values.Any(value => !value.Valid)) usage = null;
            var usedDedicated = AdapterMemory(dedicated, device.Luid);
            var usedShared = AdapterMemory(shared, device.Luid);
            var main = Series(key, device.Kind == ResourceKind.Npu ? "NPU 引擎占用" : "GPU 引擎占用", "最忙引擎", null, "%", 100, false, now, usage);
            extra.Add(Series(key + "/memory", "设备内存", "专用", "共享", "B", null, true, now, usedDedicated, usedShared));
            var index = device.Kind == ResourceKind.Npu ? npuIndex++ : gpuIndex++;
            var title = (device.Kind == ResourceKind.Npu ? "NPU " : "GPU ") + index;
            sections.Add(new(key, device.Kind, title, device.Name, $"{ResourceFormat.Percent(usage)}\n{ResourceFormat.Bytes(usedDedicated)}",
                usage.HasValue ? "按各进程在同一引擎的占用求和，设备总占用取最忙引擎。" : "设备已识别；引擎计数器尚未就绪或驱动不提供数据。",
                main, [new("占用", ResourceFormat.Percent(usage)), new("专用内存", $"{ResourceFormat.Bytes(usedDedicated)} / {ResourceFormat.Bytes(device.DedicatedLimit)}"),
                    new("共享内存", $"{ResourceFormat.Bytes(usedShared)} / {ResourceFormat.Bytes(device.SharedLimit)}"), new("驱动版本", device.Driver)], extra));
        }
        if (gpuIndex == 0) sections.Add(Unavailable("gpu", ResourceKind.Gpu, "GPU", _graphics.Status, now));
        if (npuIndex == 0) sections.Add(Unavailable("npu", ResourceKind.Npu, "NPU", "未发现支持的 NPU 设备", now));
    }

    private static double? AdapterMemory(IReadOnlyDictionary<string, double?> values, string luid)
    {
        double sum = 0; var found = false;
        foreach (var pair in values)
        {
            var match = MemoryName.Match(pair.Key);
            if (!match.Success || !string.Equals(match.Groups[1].Value, luid, StringComparison.OrdinalIgnoreCase)) continue;
            if (!pair.Value.HasValue) return null;
            found = true; sum += pair.Value.Value;
        }
        return found ? sum : null;
    }

    private PerformanceSeries Series(string key, string title, string firstLabel, string? secondLabel, string unit,
        double? maximum, bool stacked, DateTimeOffset now, double? first, double? second = null, string? note = null)
    {
        if (!_history.TryGetValue(key, out var history)) _history.Add(key, history = new());
        history.Enqueue(new(now, first, second, note));
        while (history.Count > Capacity) history.Dequeue();
        _seen[key] = now;
        return new(key, title, firstLabel, secondLabel, unit, maximum, stacked, history.ToArray());
    }

    private PerformanceSection Unavailable(string key, ResourceKind kind, string title, string status, DateTimeOffset now) =>
        new(key, kind, title, "", "—", status, Series(key, title, "占用", null, "%", 100, false, now, null), [], []);
    private static double? Add(double? first, double? second) => first.HasValue && second.HasValue ? first + second : null;
    private static double? Subtract(double? first, double? second) => first.HasValue && second.HasValue ? Math.Max(0, first.Value - second.Value) : null;
    private static int DiskIndex(string name) => int.TryParse(name.Split(' ')[0], out var index) ? index : int.MaxValue;

    public void Pause()
    {
        _counters?.Dispose(); _counters = null;
        _system.Reset(); _graphics.Reset(); _network.Clear();
    }
    public void Dispose() { Pause(); _graphics.Dispose(); _system.Dispose(); _history.Clear(); _seen.Clear(); }
}

using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Performance;
using Windows.Win32.System.SystemInformation;

namespace SystemInformer.Client.Processes;

public sealed record SystemResourceSample(double? CpuPercent = null, double? MemoryPercent = null,
    double? DiskPercent = null, double? NetworkPercent = null,
    ulong? UsedPhysicalBytes = null, ulong? TotalPhysicalBytes = null);

/// <summary>Single-reader system totals, independent of process visibility and ETW permissions.</summary>
internal sealed unsafe class SystemResourceSampler : IDisposable
{
    private PDH_HQUERY _query;
    private readonly Dictionary<string, PDH_HCOUNTER> _disks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Received, long Sent, long Timestamp, long Speed)> _network = new();
    private long _lastDiscovery;
    private bool _ready;
    private const uint MoreData = 0x800007D2;

    public SystemResourceSampler()
    {
        PDH_HQUERY query = default;
        if (PInvoke.PdhOpenQuery(default, 0, &query) != 0) return;
        _query = query;
    }

    public void Reset() { _ready = false; _network.Clear(); }

    public SystemResourceSample Capture()
    {
        double? memory = null, disk = null;
        ulong? usedPhysical = null, totalPhysical = null;
        var status = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        if (PInvoke.GlobalMemoryStatusEx(&status) && status.ullTotalPhys != 0 && status.ullAvailPhys <= status.ullTotalPhys)
        {
            usedPhysical = status.ullTotalPhys - status.ullAvailPhys;
            totalPhysical = status.ullTotalPhys;
            memory = 100d * usedPhysical.Value / totalPhysical.Value;
        }
        if (_query.Value != null)
        {
            if (_lastDiscovery == 0 || Stopwatch.GetElapsedTime(_lastDiscovery) >= TimeSpan.FromSeconds(30))
            {
                RefreshDisks();
                _lastDiscovery = Stopwatch.GetTimestamp();
            }
            if (PInvoke.PdhCollectQueryData(_query) == 0)
            {
                if (_ready)
                {
                    foreach (var counter in _disks.Values)
                        if (Read(counter) is double idle)
                            disk = Math.Max(disk ?? 0, Math.Clamp(100 - idle, 0, 100));
                }
                _ready = true;
            }
            else _ready = false;
        }
        return new(MemoryPercent: memory, DiskPercent: disk, NetworkPercent: ReadNetwork(),
            UsedPhysicalBytes: usedPhysical, TotalPhysicalBytes: totalPhysical);
    }

    private PDH_HCOUNTER AddEnglish(string path)
    {
        PDH_HCOUNTER counter = default;
        fixed (char* text = path)
            if (PInvoke.PdhAddEnglishCounter(_query, new PCWSTR(text), 0, &counter) != 0) return default;
        return counter;
    }

    private static double? Read(PDH_HCOUNTER counter)
    {
        if (counter.Value == null) return null;
        PDH_FMT_COUNTERVALUE value = default;
        if (PInvoke.PdhGetFormattedCounterValue(counter, PDH_FMT.PDH_FMT_DOUBLE, null, &value) != 0
            || value.CStatus is not (0 or 1) || !double.IsFinite(value.Anonymous.doubleValue)) return null;
        return Math.Clamp(value.Anonymous.doubleValue, 0, 100);
    }

    private void RefreshDisks()
    {
        // English wildcards must be localized before expansion, per the PDH API contract.
        var probe = AddEnglish(@"\PhysicalDisk(*)\% Idle Time");
        if (probe.Value == null) return;
        string? localized = null;
        try
        {
            uint bytes = 0;
            if (PInvoke.PdhGetCounterInfo(probe, default, &bytes, null) != MoreData || bytes == 0 || bytes > 1_048_576) return;
            var info = (PDH_COUNTER_INFO_W*)NativeMemory.Alloc(bytes);
            try
            {
                if (PInvoke.PdhGetCounterInfo(probe, default, &bytes, info) == 0) localized = info->szFullPath.ToString();
            }
            finally { NativeMemory.Free(info); }
        }
        finally { PInvoke.PdhRemoveCounter(probe); }
        if (string.IsNullOrEmpty(localized)) return;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        fixed (char* wildcard = localized)
        {
            uint length = 0;
            if (PInvoke.PdhExpandWildCardPath(default, new PCWSTR(wildcard), default, &length, 0) != MoreData
                || length == 0 || length > 1_048_576) return;
            var buffer = (char*)NativeMemory.AllocZeroed((nuint)length * sizeof(char));
            try
            {
                if (PInvoke.PdhExpandWildCardPath(default, new PCWSTR(wildcard), new PZZWSTR(buffer), &length, 0) != 0) return;
                var remaining = new ReadOnlySpan<char>(buffer, checked((int)length));
                while (remaining.Length > 0)
                {
                    var end = remaining.IndexOf('\0');
                    if (end <= 0) break;
                    var path = new string(remaining[..end]);
                    if (!path.Contains("(_Total)", StringComparison.OrdinalIgnoreCase)) paths.Add(path);
                    remaining = remaining[(end + 1)..];
                }
            }
            finally { NativeMemory.Free(buffer); }
        }
        foreach (var path in _disks.Keys.Where(path => !paths.Contains(path)).ToArray())
        {
            PInvoke.PdhRemoveCounter(_disks[path]);
            _disks.Remove(path);
        }
        foreach (var path in paths)
        {
            if (_disks.ContainsKey(path)) continue;
            PDH_HCOUNTER counter = default;
            fixed (char* text = path)
                if (PInvoke.PdhAddCounter(_query, new PCWSTR(text), 0, &counter) == 0) _disks.Add(path, counter);
        }
    }

    private double? ReadNetwork()
    {
        double? peak = null;
        var alive = new HashSet<string>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel || adapter.Speed <= 0) continue;
                alive.Add(adapter.Id);
                try
                {
                    var statistics = adapter.GetIPStatistics();
                    var now = Stopwatch.GetTimestamp();
                    var received = statistics.BytesReceived;
                    var sent = statistics.BytesSent;
                    if (_network.TryGetValue(adapter.Id, out var previous) && previous.Speed == adapter.Speed
                        && received >= previous.Received && sent >= previous.Sent)
                    {
                        var seconds = (now - previous.Timestamp) / (double)Stopwatch.Frequency;
                        if (seconds > 0)
                            peak = Math.Max(peak ?? 0, Math.Clamp(((received - previous.Received) + (double)(sent - previous.Sent))
                                * 8 / seconds / adapter.Speed * 100, 0, 100));
                    }
                    _network[adapter.Id] = (received, sent, now, adapter.Speed);
                }
                catch (NetworkInformationException) { _network.Remove(adapter.Id); }
            }
        }
        catch (NetworkInformationException) { _network.Clear(); return null; }
        foreach (var id in _network.Keys.Where(id => !alive.Contains(id)).ToArray()) _network.Remove(id);
        return peak;
    }

    public void Dispose()
    {
        if (_query.Value != null) PInvoke.PdhCloseQuery(_query);
        _query = default;
        _disks.Clear();
        _network.Clear();
    }
}

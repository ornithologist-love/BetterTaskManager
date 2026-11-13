using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Performance;

namespace SystemInformer.Client.Performance;

// Wildcard arrays keep GPU process/engine churn out of the UI and avoid a handle per process.
internal sealed unsafe class PerformanceCounters : IDisposable
{
    private PDH_HQUERY _query;
    private readonly Dictionary<string, PDH_HCOUNTER> _counters = new();
    private bool _ready;
    private long _lastDiscovery;
    private const uint MoreData = 0x800007D2;
    private const int MaximumBytes = 16 * 1024 * 1024;
    internal static readonly string[] Paths =
    [
        @"\Processor Information(_Total)\Processor Frequency",
        @"\System\Context Switches/sec", @"\System\System Calls/sec",
        @"\Processor Information(_Total)\Interrupts/sec", @"\Processor Information(_Total)\DPCs Queued/sec",
        @"\PhysicalDisk(*)\% Idle Time", @"\PhysicalDisk(*)\Disk Read Bytes/sec",
        @"\PhysicalDisk(*)\Disk Write Bytes/sec", @"\PhysicalDisk(*)\Avg. Disk sec/Transfer",
        @"\PhysicalDisk(*)\Current Disk Queue Length",
        @"\GPU Engine(*)\Utilization Percentage", @"\GPU Adapter Memory(*)\Dedicated Usage",
        @"\GPU Adapter Memory(*)\Shared Usage"
    ];

    public PerformanceCounters()
    {
        PDH_HQUERY query = default;
        if (PInvoke.PdhOpenQuery(default, 0, &query) == 0) _query = query;
    }

    public void Capture()
    {
        if (_query.Value == null) { _ready = false; return; }
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastDiscovery == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(_lastDiscovery) >= TimeSpan.FromSeconds(15))
        {
            foreach (var path in Paths.Where(path => !_counters.ContainsKey(path)))
            {
                PDH_HCOUNTER counter = default;
                fixed (char* text = path)
                    if (PInvoke.PdhAddEnglishCounter(_query, new PCWSTR(text), 0, &counter) == 0) _counters.Add(path, counter);
            }
            _lastDiscovery = now;
        }
        _ready = PInvoke.PdhCollectQueryData(_query) == 0;
    }

    public double? Value(string path)
    {
        if (!_ready || !_counters.TryGetValue(path, out var counter)) return null;
        PDH_FMT_COUNTERVALUE value = default;
        if (PInvoke.PdhGetFormattedCounterValue(counter, PDH_FMT.PDH_FMT_DOUBLE | (PDH_FMT)0x8000,
            null, &value) != 0) return null;
        return Valid(value);
    }

    public IReadOnlyDictionary<string, double?> Values(string path)
    {
        var values = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        if (!_ready || !_counters.TryGetValue(path, out var counter)) return values;
        uint bytes = 0, count = 0;
        var flags = PDH_FMT.PDH_FMT_DOUBLE | (PDH_FMT)0x8000; // PDH_FMT_NOCAP100: aggregate before capping GPU engines.
        if (PInvoke.PdhGetFormattedCounterArray(counter, flags, &bytes, &count, null) != MoreData
            || bytes == 0 || bytes > MaximumBytes) return values;
        var buffer = (byte*)NativeMemory.Alloc(bytes);
        try
        {
            var capacity = bytes;
            if (PInvoke.PdhGetFormattedCounterArray(counter, flags, &bytes, &count, (PDH_FMT_COUNTERVALUE_ITEM_W*)buffer) != 0
                || bytes > capacity || count > capacity / sizeof(PDH_FMT_COUNTERVALUE_ITEM_W)) return values;
            var items = (PDH_FMT_COUNTERVALUE_ITEM_W*)buffer;
            for (var i = 0; i < count; i++)
            {
                var text = (byte*)items[i].szName.Value;
                if (text < buffer || text >= buffer + capacity || (text - buffer) % 2 != 0) continue;
                var remaining = new ReadOnlySpan<char>((char*)text, (int)((buffer + capacity - text) / 2));
                var end = remaining.IndexOf('\0');
                if (end <= 0) continue;
                values[new string(remaining[..end])] = Valid(items[i].FmtValue);
            }
        }
        finally { NativeMemory.Free(buffer); }
        return values;
    }

    private static double? Valid(PDH_FMT_COUNTERVALUE value) => value.CStatus is 0 or 1
        && double.IsFinite(value.Anonymous.doubleValue) && value.Anonymous.doubleValue >= 0 ? value.Anonymous.doubleValue : null;

    public void Dispose()
    {
        if (_query.Value != null) PInvoke.PdhCloseQuery(_query);
        _query = default; _counters.Clear(); _ready = false;
    }
}

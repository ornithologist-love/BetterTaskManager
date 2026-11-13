namespace SystemInformer.Client.Performance;

internal enum ResourceKind { Cpu, Memory, Io, Disk, Network, Gpu, Npu }
internal sealed record Metric(string Label, string Value);
internal sealed record HistoryPoint(DateTimeOffset Time, double? First, double? Second = null, string? Note = null);
internal sealed record PerformanceSeries(string Key, string Title, string FirstLabel, string? SecondLabel,
    string Unit, double? Maximum, bool Stacked, IReadOnlyList<HistoryPoint> Points);
internal sealed record MemoryPart(string Label, double Bytes);
internal sealed record PerformanceSection(string Key, ResourceKind Kind, string Title, string Subtitle,
    string Summary, string Status, PerformanceSeries Main, IReadOnlyList<Metric> Metrics,
    IReadOnlyList<PerformanceSeries> Extra, IReadOnlyList<MemoryPart>? Composition = null);
internal sealed record PerformanceSnapshot(DateTimeOffset Time, IReadOnlyList<PerformanceSection> Sections);

internal static class ResourceFormat
{
    public static string Percent(double? value) => value is double number ? $"{number:N1}%" : "—";
    public static string Bytes(double? value)
    {
        if (value is not double number || !double.IsFinite(number) || number < 0) return "—";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var index = 0;
        while (number >= 1024 && index < units.Length - 1) { number /= 1024; index++; }
        return $"{number:N1} {units[index]}";
    }
    public static string Rate(double? value) => value.HasValue ? Bytes(value) + "/s" : "—";
    public static string Number(double? value, string suffix = "") => value is double number ? $"{number:N0}{suffix}" : "—";
}

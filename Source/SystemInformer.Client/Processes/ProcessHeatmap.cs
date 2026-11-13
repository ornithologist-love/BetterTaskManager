using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SystemInformer.Client.Processes;

/// <summary>Column-relative heat levels with a shared palette; no brushes are allocated during sampling.</summary>
internal sealed class ProcessHeatmap : IValueConverter
{
    private const int Levels = 32;
    private readonly SolidColorBrush[] _palette = Enumerable.Range(0, Levels + 1)
        .Select(_ => new SolidColorBrush(Colors.Transparent)).ToArray();

    public void SetPalette(IBrush low, IBrush high)
    {
        if (low is not ISolidColorBrush first || high is not ISolidColorBrush last) return;
        for (var level = 0; level <= Levels; level++)
        {
            var weight = level / (double)Levels;
            byte Blend(byte a, byte b) => (byte)Math.Round(a + (b - a) * weight);
            // Mutate retained brushes so visible, recycled and unchanged cells update on theme changes.
            _palette[level].Color = Color.FromRgb(Blend(first.Color.R, last.Color.R),
                Blend(first.Color.G, last.Color.G), Blend(first.Color.B, last.Color.B));
        }
    }

    public void Update(IReadOnlyList<ProcessRow> rows)
    {
        // These are our display scale floors, not undocumented Task Manager thresholds.
        double cpu = 10, memory = 2048, disk = 10, network = 100;
        foreach (var row in rows)
        {
            cpu = Math.Max(cpu, row.Cpu ?? 0);
            memory = Math.Max(memory, row.PrivateWorkingSet ?? 0);
            disk = Math.Max(disk, row.Disk ?? 0);
            network = Math.Max(network, row.Network ?? 0);
        }
        foreach (var row in rows)
            row.SetHeat(Level(row.Cpu, cpu), Level(row.PrivateWorkingSet, memory), Level(row.Disk, disk), Level(row.Network, network));
    }

    private static int Level(double? value, double scale) => value is double number && double.IsFinite(number)
        ? (int)Math.Round(Math.Clamp(number / scale, 0, 1) * Levels) : -1;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var known = value is int level && level >= 0 && level <= Levels;
        return known ? _palette[(int)value!] : Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

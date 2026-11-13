using System.ComponentModel;
using Avalonia;
using Avalonia.Media;

namespace Edirstat.Client;

internal sealed record NodeInfo(DiskEntry Entry, string Path, uint[] Ancestors, uint Offset);
internal sealed record FilePage(ulong Matched, DiskEntry[] Items);
internal sealed record MapBlock(uint Id, uint Parent, uint Depth, bool Directory, bool Free, double Header,
    double X, double Y, double W, double H, ulong Size, uint Color, string Label)
{
    public Rect Bounds => new(X, Y, W, H);
}
internal sealed record ExtensionInfo(string Extension, ulong Size, ulong Files, uint Color) : INotifyPropertyChanged
{
    private Avalonia.Media.Imaging.Bitmap? _icon;
    public Avalonia.Media.Imaging.Bitmap? Icon { get => _icon; internal set { if (ReferenceEquals(_icon, value)) return; _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); PropertyChanged?.Invoke(this, new(nameof(HasIcon))); } }
    public bool HasIcon => _icon is not null;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Name => Extension == "(no extension)" ? "无扩展名" : "." + Extension;
    public string SizeText => DiskSize.Format(Size);
    public string FilesText => Files.ToString("N0");
    public IBrush Brush => new SolidColorBrush(ExtensionPalette.Get(Color));
    public double Percentage { get; set; }
    public string PercentageText => $"{Percentage:0.0}%";
}
internal sealed class TreeRow : INotifyPropertyChanged
{
    public DiskEntry Entry { get; }
    public int Depth { get; }
    public TreeRow? Owner { get; }
    public bool More { get; }
    public uint NextOffset { get; set; }
    public uint StartOffset { get; set; }
    public uint Total { get; set; }
    public bool Loaded { get; set; }
    public List<TreeRow> Children { get; } = [];
    private bool _expanded;
    public bool Expanded { get => _expanded; set { _expanded = value; PropertyChanged?.Invoke(this, new(nameof(Arrow))); } }
    public string Arrow => More ? "+" : Expanded ? "▾" : "▸";
    public bool CanExpand => More || Entry.Directory;
    public string Name => More ? Owner is { } owner && NextOffset < owner.StartOffset ? "加载前一批目录项…" : "加载更多目录项…" : Entry.Name;
    public Thickness Indent => new(Depth * 16, 0, 0, 0);
    public string Size => More ? "" : Entry.SizeText;
    public string Percentage => More ? "" : Entry.PercentageText;
    public string Files => More ? "" : Entry.FilesText;
    public string Directories => More ? "" : Entry.DirectoriesText;
    public string Modified => More ? "" : Entry.ModifiedText;
    public TreeRow(DiskEntry entry, int depth, TreeRow? owner = null, bool more = false) { Entry = entry; Depth = depth; Owner = owner; More = more; }
    public event PropertyChangedEventHandler? PropertyChanged;
}
internal static class ExtensionPalette
{
    private static readonly Color[] Colors = [
        Color.Parse("#36A54E"), Color.Parse("#26A5DB"), Color.Parse("#B66ADD"), Color.Parse("#D89329"),
        Color.Parse("#D75E72"), Color.Parse("#456BDC"), Color.Parse("#32BDA5"), Color.Parse("#A2B831"),
        Color.Parse("#CA6C27"), Color.Parse("#954EDA"), Color.Parse("#D847AF"), Color.Parse("#4E99A4"),
        Color.Parse("#BCB02F"), Color.Parse("#337BE5"), Color.Parse("#60B63B"), Color.Parse("#A37452"),
        Color.Parse("#BD4350"), Color.Parse("#5AAEBD"), Color.Parse("#8B73DB"), Color.Parse("#C875A0"),
    ];
    internal static Color Get(uint hash) => Colors[hash % (uint)Colors.Length];
    internal static uint Hash(string extension)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(extension)) hash = unchecked((hash ^ b) * 16777619);
        return hash;
    }
}

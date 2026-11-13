using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Edirstat.Client;

internal sealed record MapImage(MapBlock[] Blocks, byte[] Pixels, int[] Hits, int Width, int Height);

// Cushion pixels are cached; hovering only redraws labels and selection outlines.
internal sealed class DiskTreemap : Control, IDisposable
{
    private MapBlock[] _blocks = [];
    private MapBlock[] _labels = [];
    private byte[] _pixels = [];
    private int[] _hits = [];
    private WriteableBitmap? _image;
    private WriteableBitmap? _filteredImage;
    private Size _sourceSize;
    private MapBlock? _hover;
    private uint _selected = uint.MaxValue;
    private MapBlock? _selectedBlock;
    private HashSet<uint> _deleted = [];
    private MapBlock[] _deletedBlocks = [];
    private uint? _extension;
    internal bool ShowNames { get; set; } = true;
    internal uint? SelectedNode => _selected == uint.MaxValue ? null : _selected;
    private readonly Pen _selectionPen = new(Brushes.White, 2);
    private readonly Pen _hoverPen = new(Brushes.Gold, 1.5);
    private readonly Pen _deletedPen = new(Brushes.Red, 2);
    internal event Action<uint>? Selected;
    internal event Action<uint>? ZoomRequested;
    internal event Action<uint?>? MenuRequested;
    internal event Action<uint>? Hovered;
    public DiskTreemap()
    {
        Focusable = true;
        ContextRequested += (_, e) =>
        {
            e.Handled = true;
            var id = e.TryGetPosition(this, out var point) ? Hit(point) is { Free: false } tile ? (uint?)tile.Id : null : _selected == uint.MaxValue ? null : _selected;
            MenuRequested?.Invoke(id);
        };
    }

    internal static MapImage Rasterize(MapBlock[] blocks, int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        var hits = new int[checked(width * height)];
        Array.Fill(hits, -1);
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 35; pixels[i + 1] = 35; pixels[i + 2] = 35; pixels[i + 3] = 255; }
        for (var blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
        {
            var tile = blocks[blockIndex];
            var left = Math.Clamp((int)Math.Ceiling(tile.X), 0, width);
            var top = Math.Clamp((int)Math.Ceiling(tile.Y), 0, height);
            var right = Math.Clamp((int)Math.Floor(tile.X + tile.W), 0, width);
            var bottom = Math.Clamp((int)Math.Floor(tile.Y + tile.H), 0, height);
            if (right <= left || bottom <= top) continue;
            var color = tile.Free ? Color.Parse("#CAD6E0") : tile.Directory ? Color.Parse("#303832") : ExtensionPalette.Get(tile.Color);
            var cushion = !tile.Directory && !tile.Free;
            var tileWidth = Math.Max(1, right - left - 1);
            var tileHeight = Math.Max(1, bottom - top - 1);
            for (var y = top; y < bottom; y++)
            {
                var dy = 2.0 * (y - top) / tileHeight - 1;
                var offset = (y * width + left) * 4;
                for (var x = left; x < right; x++, offset += 4)
                {
                    var dx = 2.0 * (x - left) / tileWidth - 1;
                    var light = cushion ? 0.48 + 0.60 * (1 - dx * dx) * (1 - dy * dy) + 0.06 * (-dx - dy) : 1;
                    if (cushion && tileWidth > 3 && tileHeight > 3 && (x == left || x == right - 1 || y == top || y == bottom - 1)) light *= 0.45;
                    pixels[offset] = (byte)Math.Clamp(color.B * light, 0, 255);
                    pixels[offset + 1] = (byte)Math.Clamp(color.G * light, 0, 255);
                    pixels[offset + 2] = (byte)Math.Clamp(color.R * light, 0, 255);
                    pixels[offset + 3] = 255;
                    hits[y * width + x] = blockIndex;
                }
            }
        }
        return new(blocks, pixels, hits, width, height);
    }

    internal void SetImage(MapImage? image)
    {
        _image?.Dispose(); _image = null;
        _filteredImage?.Dispose(); _filteredImage = null;
        _blocks = image?.Blocks ?? [];
        _deletedBlocks = _blocks.Where(tile => _deleted.Contains(tile.Id)).ToArray();
        _labels = _blocks.Where(tile => tile.Label.Length > 0).ToArray();
        _pixels = image?.Pixels ?? [];
        _hits = image?.Hits ?? [];
        _hover = null;
        _selectedBlock = Array.Find(_blocks, tile => tile.Id == _selected);
        if (image is not null)
        {
            _sourceSize = new(image.Width, image.Height);
            _image = Bitmap(image.Pixels, image.Width, image.Height);
            RebuildFilter();
        }
        InvalidateVisual();
    }
    private static WriteableBitmap Bitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var locked = bitmap.Lock();
        for (var row = 0; row < height; row++)
            Marshal.Copy(pixels, row * width * 4, locked.Address + row * locked.RowBytes, width * 4);
        return bitmap;
    }
    internal void Highlight(uint id) { _selected = id; _selectedBlock = Array.Find(_blocks, tile => tile.Id == id); InvalidateVisual(); }
    internal void SetDeleted(IEnumerable<uint> ids)
    {
        _deleted = ids.ToHashSet();
        _deletedBlocks = _blocks.Where(tile => !tile.Free && _deleted.Contains(tile.Id)).ToArray();
        InvalidateVisual();
    }
    internal void Filter(string? extension)
    {
        var hash = extension is null ? (uint?)null : ExtensionPalette.Hash(extension);
        if (_extension == hash) return;
        _extension = hash; RebuildFilter(); InvalidateVisual();
    }
    // Cache the type highlight as pixels too; pointer movement must not draw
    // hundreds of thousands of separate dimming rectangles.
    private void RebuildFilter()
    {
        _filteredImage?.Dispose(); _filteredImage = null;
        if (_image is null || _extension is not { } extension) return;
        var pixels = (byte[])_pixels.Clone();
        for (var i = 0; i < _hits.Length; i++)
        {
            var index = _hits[i];
            if (index < 0 || _blocks[index].Directory || _blocks[index].Free || _blocks[index].Color == extension) continue;
            var offset = i * 4;
            pixels[offset] = (byte)((pixels[offset] * 70 + 20 * 185) / 255);
            pixels[offset + 1] = (byte)((pixels[offset + 1] * 70 + 22 * 185) / 255);
            pixels[offset + 2] = (byte)((pixels[offset + 2] * 70 + 18 * 185) / 255);
        }
        _filteredImage = Bitmap(pixels, (int)_sourceSize.Width, (int)_sourceSize.Height);
    }
    private Rect Scale(Rect rect) => new(rect.X * Bounds.Width / _sourceSize.Width, rect.Y * Bounds.Height / _sourceSize.Height,
        rect.Width * Bounds.Width / _sourceSize.Width, rect.Height * Bounds.Height / _sourceSize.Height);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_image is null) { context.FillRectangle(this.TryFindResource("BgSurfaceBrush", ActualThemeVariant, out var background) && background is IBrush brush ? brush : Brushes.Transparent, new Rect(Bounds.Size)); return; }
        context.DrawImage(_filteredImage ?? _image, new Rect(Bounds.Size));
        foreach (var tile in _labels)
        {
            if (!ShowNames && !tile.Free) continue;
            var rect = Scale(tile.Bounds);
            if (rect.Width > 100 && (tile.Directory ? tile.Header > 0 : rect.Height > 38))
            {
                var label = tile.Directory ? tile.Label : $"{tile.Label}\n{DiskSize.Format(tile.Size)}";
                var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 11, tile.Free ? Brushes.Black : Brushes.White);
                var clip = tile.Directory ? new Rect(rect.X, rect.Y, rect.Width, Math.Min(rect.Height, tile.Header * Bounds.Height / _sourceSize.Height)) : rect;
                using (context.PushClip(clip)) context.DrawText(text, rect.TopLeft + new Vector(3, 1));
            }
        }
        if (_selectedBlock is { } selected) Outline(context, _selectionPen, Scale(selected.Bounds));
        if (_hover is { } hover) Outline(context, _hoverPen, Scale(hover.Bounds));
        foreach (var tile in _deletedBlocks) Outline(context, _deletedPen, Scale(tile.Bounds));
    }
    private static void Outline(DrawingContext context, Pen pen, Rect rect) =>
        context.DrawRectangle(null, pen, rect.Deflate(Math.Min(1, Math.Min(rect.Width, rect.Height) / 4)));
    private MapBlock? Hit(Point point)
    {
        if (_image is null || Bounds.Width <= 0 || Bounds.Height <= 0) return null;
        var x = (int)Math.Floor(point.X * _sourceSize.Width / Bounds.Width);
        var y = (int)Math.Floor(point.Y * _sourceSize.Height / Bounds.Height);
        if (x < 0 || y < 0 || x >= (int)_sourceSize.Width || y >= (int)_sourceSize.Height) return null;
        var index = _hits[y * (int)_sourceSize.Width + x];
        return index < 0 ? null : _blocks[index];
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var tile = Hit(e.GetPosition(this));
        if (ReferenceEquals(tile, _hover)) return;
        _hover = tile;
        if (tile is not null) { ToolTip.SetTip(this, $"{(tile.Label.Length == 0 ? "" : tile.Label + "\n")}{DiskSize.Format(tile.Size)}{(tile.Directory ? "\n双击放大" : "")}"); Hovered?.Invoke(tile.Id); }
        InvalidateVisual();
    }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _hover = null; InvalidateVisual(); }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) { Focus(); return; }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Hit(e.GetPosition(this)) is not { } tile) return;
        if (tile.Free) { _selected = uint.MaxValue; _selectedBlock = null; Selected?.Invoke(uint.MaxValue); InvalidateVisual(); return; }
        if (e.ClickCount == 2) ZoomRequested?.Invoke(tile.Id);
        else { _selected = tile.Id; _selectedBlock = tile; Selected?.Invoke(tile.Id); InvalidateVisual(); }
    }
    public void Dispose() { SetImage(null); }
}

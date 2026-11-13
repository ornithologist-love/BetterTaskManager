using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SystemInformer.Client.Processes;

/// <summary>UI-owned bitmaps are created only when a visible cell asks for one.</summary>
internal sealed class ProcessIconConverter : IValueConverter, IDisposable
{
    private readonly Dictionary<ProcessIcon, WriteableBitmap> _images = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ProcessIcon icon) return null;
        if (_images.TryGetValue(icon, out var image)) return image;
        image = new(new PixelSize(icon.Width, icon.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = image.Lock())
        {
            var stride = icon.Width * 4;
            for (var row = 0; row < icon.Height; row++)
                Marshal.Copy(icon.Pixels, row * stride, buffer.Address + row * buffer.RowBytes, stride);
        }
        _images.Add(icon, image);
        return image;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    public void Trim(IEnumerable<ProcessIcon?> icons)
    {
        var alive = icons.Where(icon => icon != null).ToHashSet();
        foreach (var icon in _images.Keys.Where(icon => !alive.Contains(icon)).ToArray())
        {
            _images[icon].Dispose();
            _images.Remove(icon);
        }
    }
    public void Dispose()
    {
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
    }
}

internal sealed class ProcessIndentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new Thickness(value is int depth ? depth * 16 : 0, 0, 0, 0);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

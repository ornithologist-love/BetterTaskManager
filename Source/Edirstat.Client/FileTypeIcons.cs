using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Edirstat.Client;

// One lazy Shell worker per view. Both icons and missing associations are cached;
// neither scanning nor the UI waits for the Shell to finish looking up a type.
internal sealed class FileTypeIcons : IDisposable
{
    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ExtensionInfo>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<string> _requests = new();
    private bool _started;
    private volatile bool _disposed;

    internal void Request(IEnumerable<ExtensionInfo> entries)
    {
        if (_disposed || !OperatingSystem.IsWindows()) return;
        foreach (var entry in entries)
        {
            if (entry.Extension == "(no extension)" || string.IsNullOrWhiteSpace(entry.Extension)) continue;
            if (_cache.TryGetValue(entry.Extension, out var icon)) { entry.Icon = icon; continue; }
            if (_pending.TryGetValue(entry.Extension, out var waiting)) { waiting.Add(entry); continue; }
            _pending.Add(entry.Extension, [entry]);
            _requests.Add(entry.Extension);
        }
        if (_started || _pending.Count == 0) return;
        _started = true;
        var thread = new Thread(Load) { IsBackground = true, Name = "edirstat-type-icons" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private void Load()
    {
        var initialized = NativeIcons.CoInitializeEx(0, 2) >= 0;
        try
        {
            var unknown = initialized ? NativeIcons.UnknownIndex() : null;
            foreach (var extension in _requests.GetConsumingEnumerable())
            {
                if (_disposed) break;
                byte[]? pixels = null;
                try { if (unknown is { } index) pixels = NativeIcons.Read(extension, index); }
                catch (Exception) { /* A broken association retains the color swatch. */ }
                var completed = pixels;
                Dispatcher.UIThread.Post(() => Publish(extension, completed));
            }
        }
        finally
        {
            if (initialized) NativeIcons.CoUninitialize();
            _requests.Dispose();
        }
    }

    private void Publish(string extension, byte[]? pixels)
    {
        if (_disposed) return;
        Bitmap? icon = null;
        if (pixels is not null)
        {
            var bitmap = new WriteableBitmap(new PixelSize(NativeIcons.Size, NativeIcons.Size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var locked = bitmap.Lock())
                for (var row = 0; row < NativeIcons.Size; row++)
                    Marshal.Copy(pixels, row * NativeIcons.Size * 4, locked.Address + row * locked.RowBytes, NativeIcons.Size * 4);
            icon = bitmap;
        }
        _cache[extension] = icon;
        if (_pending.Remove(extension, out var waiting)) foreach (var entry in waiting) entry.Icon = icon;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _requests.CompleteAdding();
        if (!_started) _requests.Dispose();
        foreach (var bitmap in _cache.Values) bitmap?.Dispose();
        _cache.Clear(); _pending.Clear();
    }

    private static class NativeIcons
    {
        internal const int Size = 32;
        private const uint UseAttributes = 0x10, Icon = 0x100, SystemIndex = 0x4000, NormalFile = 0x80;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo
        {
            public nint Icon;
            public int Index;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapHeader
        {
            public uint HeaderSize;
            public int Width, Height;
            public ushort Planes, Bits;
            public uint Compression, Bytes;
            public int XResolution, YResolution;
            public uint Colors, ImportantColors;
        }
        [DllImport("ole32.dll", ExactSpelling = true)] internal static extern int CoInitializeEx(nint reserved, uint flags);
        [DllImport("ole32.dll", ExactSpelling = true)] internal static extern void CoUninitialize();
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint SHGetFileInfoW(string path, uint attributes, out FileInfo info, uint size, uint flags);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern bool DestroyIcon(nint icon);
        [DllImport("user32.dll", ExactSpelling = true)] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
        [DllImport("gdi32.dll", ExactSpelling = true)] private static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll", ExactSpelling = true)] private static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll", ExactSpelling = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapHeader info, uint usage, out nint pixels, nint section, uint offset);
        [DllImport("gdi32.dll", ExactSpelling = true)] private static extern nint SelectObject(nint dc, nint bitmap);
        [DllImport("gdi32.dll", ExactSpelling = true)] private static extern bool DeleteObject(nint bitmap);
        [DllImport("gdi32.dll", ExactSpelling = true)] private static extern bool GdiFlush();

        internal static int? UnknownIndex() => SHGetFileInfoW(".edirstat-unknown-74e96847", NormalFile, out var info, (uint)Marshal.SizeOf<FileInfo>(), UseAttributes | SystemIndex) == 0 ? null : info.Index;

        internal static byte[]? Read(string extension, int unknownIndex)
        {
            // USEFILEATTRIBUTES queries only the association, never a scanned file.
            var result = SHGetFileInfoW("." + extension.TrimStart('.'), NormalFile, out var info, (uint)Marshal.SizeOf<FileInfo>(), UseAttributes | Icon);
            try
            {
                if (result == 0 || info.Icon == 0 || info.Index == unknownIndex) return null;
                return Pixels(info.Icon);
            }
            finally { if (info.Icon != 0) DestroyIcon(info.Icon); }
        }

        private static byte[]? Pixels(nint icon)
        {
            var dc = CreateCompatibleDC(0);
            if (dc == 0) return null;
            nint bitmap = 0, previous = 0;
            try
            {
                var header = new BitmapHeader { HeaderSize = (uint)Marshal.SizeOf<BitmapHeader>(), Width = Size, Height = -Size, Planes = 1, Bits = 32 };
                bitmap = CreateDIBSection(dc, ref header, 0, out var address, 0, 0);
                if (bitmap == 0 || address == 0) return null;
                previous = SelectObject(dc, bitmap);
                if (previous == 0 || previous == -1) return null;
                var black = new byte[Size * Size * 4];
                var white = new byte[black.Length];
                Marshal.Copy(black, 0, address, black.Length);
                if (!DrawIconEx(dc, 0, 0, icon, Size, Size, 0, 0, 3)) return null;
                GdiFlush(); Marshal.Copy(address, black, 0, black.Length);
                Array.Fill(white, (byte)255); Marshal.Copy(white, 0, address, white.Length);
                if (!DrawIconEx(dc, 0, 0, icon, Size, Size, 0, 0, 3)) return null;
                GdiFlush(); Marshal.Copy(address, white, 0, white.Length);
                // Recover premultiplied alpha from black/white backgrounds. This
                // handles both modern alpha icons and legacy monochrome masks.
                for (var i = 0; i < black.Length; i += 4)
                {
                    var alpha = 255 - Math.Clamp(Math.Max(white[i] - black[i], Math.Max(white[i + 1] - black[i + 1], white[i + 2] - black[i + 2])), 0, 255);
                    black[i + 3] = (byte)alpha;
                    for (var channel = 0; channel < 3; channel++) black[i + channel] = (byte)Math.Min(black[i + channel], alpha);
                }
                return black;
            }
            finally
            {
                if (previous != 0 && previous != -1) SelectObject(dc, previous);
                if (bitmap != 0) DeleteObject(bitmap);
                DeleteDC(dc);
            }
        }
    }
}

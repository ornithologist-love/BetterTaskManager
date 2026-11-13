using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace SystemInformer.Client.Processes;

/// <summary>Premultiplied BGRA pixels. No UI or native handles are retained in snapshots.</summary>
public sealed record ProcessIcon(int Width, int Height, byte[] Pixels);

internal sealed unsafe class ProcessIconCache
{
    private readonly Dictionary<string, ProcessIcon?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private ProcessIcon? _fallback;
    private bool _fallbackLoaded;

    public ProcessIcon? Get(string path)
    {
        if (!_fallbackLoaded) { _fallback = ReadIcon("application.exe", true); _fallbackLoaded = true; }
        if (string.IsNullOrEmpty(path)) return _fallback;
        if (!_icons.TryGetValue(path, out var icon))
        {
            icon = ReadIcon(path, false) ?? _fallback;
            _icons.Add(path, icon);
        }
        return icon;
    }

    public void Trim(IEnumerable<string> paths)
    {
        var alive = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _icons.Keys.Where(path => !alive.Contains(path)).ToArray()) _icons.Remove(path);
    }

    public void Clear() { _icons.Clear(); _fallback = null; }

    private static ProcessIcon? ReadIcon(string path, bool generic)
    {
        var comInitialized = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED).Value >= 0;
        SHFILEINFOW info = default;
        HDC screen = default, memory = default;
        HBITMAP bitmap = default;
        HGDIOBJ previous = default;
        try
        {
            fixed (char* text = path)
            {
                if (PInvoke.SHGetFileInfo(new PCWSTR(text), (FILE_FLAGS_AND_ATTRIBUTES)0x80, &info,
                    (uint)sizeof(SHFILEINFOW), (SHGFI_FLAGS)(0x100 | 0x1 | (generic ? 0x10 : 0))) == 0
                    || info.hIcon.Value == null) return null;
            }
            screen = PInvoke.GetDC(default);
            if (screen.Value == null) return null;
            memory = PInvoke.CreateCompatibleDC(screen);
            if (memory.Value == null) return null;
            const int width = 16, height = 16, length = width * height * 4;
            var bitmapInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height,
                    biPlanes = 1, biBitCount = 32
                }
            };
            void* pixels = null;
            bitmap = PInvoke.CreateDIBSection(screen, &bitmapInfo, DIB_USAGE.DIB_RGB_COLORS, &pixels, default, 0);
            if (bitmap.Value == null || pixels == null) return null;
            previous = PInvoke.SelectObject(memory, new HGDIOBJ(bitmap.Value));
            if (previous.Value == null || (nint)previous.Value == -1) return null;
            var buffer = new Span<byte>(pixels, length);
            buffer.Clear();
            if (!PInvoke.DrawIconEx(memory, 0, 0, info.hIcon, width, height, 0, default, (DI_FLAGS)3)) return null;
            PInvoke.GdiFlush();
            var black = buffer.ToArray();
            buffer.Fill(255);
            if (!PInvoke.DrawIconEx(memory, 0, 0, info.hIcon, width, height, 0, default, (DI_FLAGS)3)) return null;
            PInvoke.GdiFlush();
            // Black/white renders recover alpha for both modern alpha icons and legacy AND-mask icons.
            for (var i = 0; i < length; i += 4)
            {
                var difference = Math.Max(buffer[i] - black[i], Math.Max(buffer[i + 1] - black[i + 1], buffer[i + 2] - black[i + 2]));
                black[i + 3] = (byte)Math.Clamp(255 - difference, 0, 255);
            }
            return new(width, height, black);
        }
        finally
        {
            if (previous.Value != null && (nint)previous.Value != -1) PInvoke.SelectObject(memory, previous);
            if (bitmap.Value != null) PInvoke.DeleteObject(new HGDIOBJ(bitmap.Value));
            if (memory.Value != null) PInvoke.DeleteDC(memory);
            if (screen.Value != null) PInvoke.ReleaseDC(default, screen);
            if (info.hIcon.Value != null) PInvoke.DestroyIcon(info.hIcon);
            if (comInitialized) PInvoke.CoUninitialize();
        }
    }
}

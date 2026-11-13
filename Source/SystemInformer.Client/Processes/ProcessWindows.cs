using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace SystemInformer.Client.Processes;

internal sealed unsafe class ProcessWindows
{
    private readonly HashSet<uint> _visible = new(), _hung = new();
    private readonly Dictionary<uint, List<string>> _titles = new();
    private uint _foreground;
    private bool _enumerated;

    public void Capture()
    {
        _visible.Clear();
        _hung.Clear();
        _titles.Clear();
        uint foreground = 0;
        PInvoke.GetWindowThreadProcessId(PInvoke.GetForegroundWindow(), &foreground);
        _foreground = foreground;
        var context = GCHandle.Alloc(this);
        try { _enumerated = PInvoke.EnumWindows(&OnWindow, new LPARAM(GCHandle.ToIntPtr(context))); }
        finally { context.Free(); }
    }

    public string Role(uint id) => !_enumerated ? "未知"
        : _visible.Contains(id) ? (id == _foreground ? "前台" : "有窗口") : "后台";
    public bool IsHung(uint id) => _hung.Contains(id);
    public string Titles(uint id) => _titles.TryGetValue(id, out var titles) ? string.Join("\n", titles) : "";

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static BOOL OnWindow(HWND window, LPARAM parameter)
    {
        try
        {
            // Task Manager's published app rule is a visible window, not taskbar eligibility.
            // Owned/tool windows and windows on another virtual desktop must not be discarded here.
            if (!PInvoke.IsWindowVisible(window) || window == PInvoke.GetShellWindow()) return true;
            uint id = 0;
            PInvoke.GetWindowThreadProcessId(window, &id);
            if (id == 0) return true;
            if (GCHandle.FromIntPtr(parameter.Value).Target is ProcessWindows owner)
            {
                owner._visible.Add(id);
                if (PInvoke.IsHungAppWindow(window)) owner._hung.Add(id);
                char* text = stackalloc char[1024];
                var length = PInvoke.GetWindowText(window, new PWSTR(text), 1024);
                if (length > 0)
                {
                    var title = new string(text, 0, length);
                    if (!owner._titles.TryGetValue(id, out var titles)) owner._titles[id] = titles = new();
                    if (!titles.Contains(title)) titles.Add(title);
                }
            }
        }
        catch { /* Exceptions must never cross the native enumeration callback. */ }
        return true;
    }
}

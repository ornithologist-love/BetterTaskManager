using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Services;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows10.0")]

namespace SystemInformer.Client.Interop;

internal sealed unsafe class ProcessHandle : SafeHandle
{
    public ProcessHandle(HANDLE value) : base(IntPtr.Zero, true) => SetHandle((nint)value.Value);
    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
    public HANDLE Value => new((void*)handle);
    protected override bool ReleaseHandle() => PInvoke.CloseHandle(Value);
}

internal sealed unsafe class ServiceHandle : SafeHandle
{
    public ServiceHandle(SC_HANDLE value) : base(IntPtr.Zero, true) => SetHandle((nint)value.Value);
    public override bool IsInvalid => handle == IntPtr.Zero;
    public SC_HANDLE Value => new((void*)handle);
    protected override bool ReleaseHandle() => PInvoke.CloseServiceHandle(Value);
}

internal static unsafe class NativeErrors
{
    public static Win32Exception Last(string operation) => new(Marshal.GetLastWin32Error(), operation);

    public static string Text(PWSTR value, byte* buffer, int size)
    {
        if (value.Value == null) return "";
        var pointer = (byte*)value.Value;
        if (pointer < buffer || pointer >= buffer + size || (pointer - buffer) % 2 != 0)
            throw new InvalidDataException("Windows 返回了无效的字符串地址。");
        var chars = new ReadOnlySpan<char>(value.Value, (int)((buffer + size - pointer) / 2));
        var length = chars.IndexOf('\0');
        if (length < 0) throw new InvalidDataException("Windows 返回的字符串缺少结束符。");
        return new string(chars[..length]);
    }

    public static string[] MultiText(PWSTR value, byte* buffer, int size)
    {
        var result = new List<string>();
        var pointer = value.Value;
        while (pointer != null)
        {
            var text = Text(new PWSTR(pointer), buffer, size);
            if (text.Length == 0) break;
            result.Add(text);
            pointer += text.Length + 1;
        }
        return result.ToArray();
    }
}

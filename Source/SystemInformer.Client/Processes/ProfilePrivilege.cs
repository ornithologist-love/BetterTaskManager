using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace SystemInformer.Client.Processes;

/// <summary>Enables only the profiling privilege in this process, and restores its previous state.</summary>
internal sealed unsafe class ProfilePrivilege : IDisposable
{
    private HANDLE _token;
    private TOKEN_PRIVILEGES _previous;
    public ProfilePrivilege()
    {
        HANDLE token = default;
        if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess(), (TOKEN_ACCESS_MASK)0x28, &token)) return;
        _token = token;
        LUID luid = default;
        const string name = "SeSystemProfilePrivilege";
        fixed (char* text = name)
            if (!PInvoke.LookupPrivilegeValue(default, new PCWSTR(text), &luid)) return;
        var requested = new TOKEN_PRIVILEGES { PrivilegeCount = 1 };
        requested.Privileges[0] = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED };
        TOKEN_PRIVILEGES previous = default;
        uint returned = 0;
        if (PInvoke.AdjustTokenPrivileges(token, false, &requested, (uint)sizeof(TOKEN_PRIVILEGES), &previous, &returned)
            && Marshal.GetLastWin32Error() == 0) _previous = previous;
    }
    public void Dispose()
    {
        if (_token.Value == null) return;
        if (_previous.PrivilegeCount != 0)
        {
            var previous = _previous;
            PInvoke.AdjustTokenPrivileges(_token, false, &previous, 0, null, null);
        }
        PInvoke.CloseHandle(_token);
        _token = default;
    }
}

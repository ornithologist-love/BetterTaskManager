using System.Diagnostics;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using SystemInformer.Client.Interop;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace SystemInformer.Client.Processes;

// Capture identity when the menu opens; refreshes must not retarget an action.
internal sealed record ProcessTarget(uint Id, ulong? Created, string Name, string Path, bool IsSynthetic)
{
    public bool CanOpen => !IsSynthetic && Id != 0 && Created.HasValue && !string.IsNullOrWhiteSpace(Path);
    public bool CanTerminate => !IsSynthetic && Created.HasValue && Id is not (0 or 4)
        && Id != (uint)Environment.ProcessId;
}

internal static class ProcessOperations
{
    public static Task TerminateAsync(ProcessTarget target, CancellationToken token) =>
        Task.Run(() => Terminate(target, token), token);

    public static Task OpenLocationAsync(ProcessTarget target, CancellationToken token) =>
        Task.Run(() => OpenLocation(target, token), token);

    public static async Task OpenPropertiesAsync(ProcessTarget target, CancellationToken token)
    {
        var path = await Task.Run(() => VerifiedImagePath(target, token), token).ConfigureAwait(false);
        // Shell property handlers need an STA that stays alive and pumps messages.
        // The application's UI thread provides that lifetime; a short-lived STA does not.
        await Dispatcher.UIThread.InvokeAsync(() => OpenProperties(path, token));
    }

    private static unsafe ProcessHandle OpenVerified(ProcessTarget target, PROCESS_ACCESS_RIGHTS access)
    {
        if (target.IsSynthetic || target.Id == 0 || !target.Created.HasValue)
            throw new InvalidOperationException("无法确认目标进程的身份。");
        var handle = new ProcessHandle(PInvoke.OpenProcess(access | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            false, target.Id));
        try
        {
            if (handle.IsInvalid) throw NativeErrors.Last("打开目标进程失败");
            FILETIME created = default, exited = default, kernel = default, user = default;
            if (!PInvoke.GetProcessTimes(handle.Value, &created, &exited, &kernel, &user))
                throw NativeErrors.Last("读取目标进程身份失败");
            var ticks = ((ulong)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
            if (ticks != target.Created.Value)
                throw new InvalidOperationException("原进程已退出，此 PID 已被其他进程使用。请重新选择。");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static unsafe void Terminate(ProcessTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!target.CanTerminate) throw new InvalidOperationException("此进程不支持强制结束。");
        using var handle = OpenVerified(target, PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE);
        if (PInvoke.WaitForSingleObject(handle.Value, 0) == 0)
            throw new InvalidOperationException("目标进程已退出。");
        BOOL critical = default;
        if (!PInvoke.IsProcessCritical(handle.Value, &critical))
            throw NativeErrors.Last("检查关键系统进程失败");
        if (critical) throw new InvalidOperationException("不能结束 Windows 关键系统进程。");
        token.ThrowIfCancellationRequested();
        // Terminate only this handle's process, never traverse the displayed children.
        if (!PInvoke.TerminateProcess(handle.Value, 1)) throw NativeErrors.Last("强制结束进程失败");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var result = PInvoke.WaitForSingleObject(handle.Value, 100);
            if (result == 0) return;
            if ((uint)result != 258) throw NativeErrors.Last("等待进程退出失败");
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
                throw new TimeoutException("已请求强制结束，但十秒内尚未确认进程退出。");
        }
    }

    private static unsafe string VerifiedImagePath(ProcessTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!target.CanOpen) throw new InvalidOperationException("目标进程的可执行文件路径不可读取。");
        using var handle = OpenVerified(target, (PROCESS_ACCESS_RIGHTS)0);
        char* path = stackalloc char[32768];
        uint length = 32768;
        if (!PInvoke.QueryFullProcessImageName(handle.Value, 0, new PWSTR(path), &length))
            throw NativeErrors.Last("读取可执行文件路径失败");
        if (length == 0 || length >= 32768) throw new InvalidDataException("Windows 返回了无效的可执行文件路径。");
        path[length] = '\0';
        return new string(path, 0, (int)length);
    }

    private static unsafe void OpenProperties(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var initialized = PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED | COINIT.COINIT_DISABLE_OLE1DDE);
        Marshal.ThrowExceptionForHR(initialized.Value);
        try
        {
            fixed (char* file = path)
            {
                if (!PInvoke.SHObjectProperties(default, SHOP_TYPE.SHOP_FILEPATH, new PCWSTR(file), default))
                    throw new InvalidOperationException("Windows 无法打开此可执行文件的属性。");
            }
        }
        finally { PInvoke.CoUninitialize(); }
    }

    private static unsafe void OpenLocation(ProcessTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!target.CanOpen) throw new InvalidOperationException("目标进程的可执行文件路径不可读取。");
        using var handle = OpenVerified(target, (PROCESS_ACCESS_RIGHTS)0);
        char* path = stackalloc char[32768];
        uint length = 32768;
        if (!PInvoke.QueryFullProcessImageName(handle.Value, 0, new PWSTR(path), &length))
            throw NativeErrors.Last("读取可执行文件路径失败");
        if (length == 0 || length >= 32768) throw new InvalidDataException("Windows 返回了无效的可执行文件路径。");
        var initialized = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        // RPC_E_CHANGED_MODE means this thread already has a different COM apartment.
        if (initialized.Value < 0 && initialized.Value != unchecked((int)0x80010106))
            Marshal.ThrowExceptionForHR(initialized.Value);
        ITEMIDLIST* item = null;
        try
        {
            path[length] = '\0';
            Marshal.ThrowExceptionForHR(PInvoke.SHParseDisplayName(new PWSTR(path), null, &item, 0, null).Value);
            token.ThrowIfCancellationRequested();
            // A full file PIDL with zero child items opens its parent and selects the file.
            Marshal.ThrowExceptionForHR(PInvoke.SHOpenFolderAndSelectItems(item, 0, null, 0).Value);
        }
        finally
        {
            if (item != null) PInvoke.CoTaskMemFree(item);
            if (initialized.Value >= 0) PInvoke.CoUninitialize();
        }
    }
}

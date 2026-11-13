using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Com;

namespace SystemInformer.Client.Services;

// The Services snap-in owns the real property sheet, including its account/password UI.
// No credentials or native service property settings enter this application's controls.
internal static class NativeServiceProperties
{
    public static Task OpenAsync(WindowsServices services, string name, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { Open(services, name, token); completion.TrySetResult(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "SystemInformer.ServiceProperties" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static unsafe void Open(WindowsServices services, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var displayName = services.GetDisplayName(name);
        var initialized = PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED | COINIT.COINIT_DISABLE_OLE1DDE);
        Marshal.ThrowExceptionForHR(initialized.Value);
        var owned = new List<object>();
        object Own(object value) { owned.Add(value); return value; }
        try
        {
            var type = Type.GetTypeFromProgID("MMC20.Application", throwOnError: true)!;
            dynamic application = Own(Activator.CreateInstance(type) ?? throw new InvalidOperationException("无法创建 Windows 服务控制台。"));
            // Hand the native console to the user so releasing our automation references
            // cannot tear down the property window immediately after the method returns.
            application.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "services.msc"));
            dynamic document = Own((object)application.Document);
            dynamic view = Own((object)document.ActiveView);
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                dynamic nodes = (object)view.ListItems;
                object? selected = null;
                try
                {
                    int count = nodes.Count;
                    for (var index = 1; index <= count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        dynamic node = (object)nodes.Item(index);
                        try
                        {
                            if (string.Equals((string)node.Name, displayName, StringComparison.OrdinalIgnoreCase))
                            {
                                if (selected != null) throw new InvalidOperationException("服务名称匹配不唯一，无法安全打开属性。");
                                selected = (object)node;
                                owned.Add(selected);
                                node = null!;
                            }
                        }
                        finally { Release((object?)node); }
                    }
                    if (selected != null)
                    {
                        token.ThrowIfCancellationRequested();
                        view.Select(selected);
                        application.UserControl = 1;
                        view.DisplaySelectionPropertySheet();
                        return;
                    }
                }
                finally { Release((object)nodes); }
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(8))
                    throw new InvalidOperationException("Windows 服务控制台中未找到此服务，请刷新后重试。");
                if (token.WaitHandle.WaitOne(100)) token.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            for (var index = owned.Count - 1; index >= 0; index--) Release(owned[index]);
            PInvoke.CoUninitialize();
        }
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}

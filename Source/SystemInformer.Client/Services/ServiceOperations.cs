using System.ComponentModel;
using System.Diagnostics;

namespace SystemInformer.Client.Services;

/// <summary>Invoked only by a user action. No separate helper project or bundled runtime.</summary>
public sealed class ServiceOperations(WindowsServices services)
{
    public async Task ExecuteAsync(string name, ServiceOperation operation, CancellationToken token)
    {
        if (operation == ServiceOperation.Restart)
        {
            await RequestAsync(name, ServiceOperation.Stop, token).ConfigureAwait(false);
            await WaitAsync(name, 1, token).ConfigureAwait(false);
            await RequestAsync(name, ServiceOperation.Start, token).ConfigureAwait(false);
            await WaitAsync(name, 4, token).ConfigureAwait(false);
            return;
        }
        await RequestAsync(name, operation, token).ConfigureAwait(false);
        await WaitAsync(name, operation switch { ServiceOperation.Stop => 1u, ServiceOperation.Pause => 7u, _ => 4u }, token).ConfigureAwait(false);
    }

    public async Task SetStartupAsync(string name, StartupOption startup, CancellationToken token)
    {
        WindowsServices.ValidateStartup(startup);
        try { await Task.Run(() => services.SetStartup(name, startup), token).ConfigureAwait(false); }
        catch (Win32Exception error) when (error.NativeErrorCode == 5)
        {
            var value = startup.Delayed ? "delayed-auto" : startup.Type switch { 2 => "auto", 3 => "demand", _ => "disabled" };
            await ElevatedScAsync(["config", name, "start=", value], token).ConfigureAwait(false);
        }
        var matches = await Task.Run(() => services.MatchesStartup(name, startup), token).ConfigureAwait(false);
        if (!matches) throw new InvalidOperationException("服务启动类型未变更为所选值，请刷新后检查。");
    }

    private async Task RequestAsync(string name, ServiceOperation operation, CancellationToken token)
    {
        try { await Task.Run(() => services.Request(name, operation), token).ConfigureAwait(false); }
        catch (Win32Exception error) when (error.NativeErrorCode == 5)
        {
            var verb = operation switch
            {
                ServiceOperation.Start => "start", ServiceOperation.Stop => "stop",
                ServiceOperation.Pause => "pause", ServiceOperation.Continue => "continue",
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            await ElevatedScAsync([verb, name], token).ConfigureAwait(false);
        }
    }

    private async Task WaitAsync(string name, uint expected, CancellationToken token)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(45))
        {
            token.ThrowIfCancellationRequested();
            var status = await Task.Run(() => services.GetStatus(name), token).ConfigureAwait(false);
            if (status.State == expected) return;
            if (expected == 4 && status.State == 1)
                throw new InvalidOperationException($"服务未进入运行状态，已停止；Windows 错误码 {status.ExitCode}。");
            await Task.Delay(350, token).ConfigureAwait(false);
        }
        throw new TimeoutException("服务在 45 秒内未达到目标状态；操作可能仍在进行，请刷新查看。");
    }

    private static async Task ElevatedScAsync(string[] arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动服务管理操作。");
        // Drain accepted commands even when the panel stops. Canceling observation must not kill
        // sc.exe between SCM configuration writes or leave an untracked elevated child.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new Win32Exception(process.ExitCode, "管理员服务操作失败。");
        token.ThrowIfCancellationRequested();
    }
}

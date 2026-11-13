using Heron.ClientFramework.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using SystemInformer.PluginFacade;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows10.0")]

namespace Heron.Plugins.BTM;

public sealed class SystemInformerModule : IClientPluginModule
{
    private SystemInformerFacade? _facade;
    private IDisposable? _page;

    public void ConfigureServices(IServiceCollection services) => services.AddSystemInformerFacade();

    public Task StartAsync(IClientPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Native pages need neither startup preloading nor a permanently mounted visual tree.
        _page = context.RegisterPage(new ClientPluginPage(
            "better-systemtaskmanager", "任务管理器", "Pulse", 60,
            () => (_facade ??= context.Services.GetRequiredService<SystemInformerFacade>()).Start()));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _page?.Dispose();
        _page = null;
        try { if (_facade != null) await _facade.StopAsync(); }
        finally { _facade = null; }
    }
}

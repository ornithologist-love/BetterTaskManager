using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using SystemInformer.Client;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows10.0")]

namespace SystemInformer.PluginFacade;

public static class SystemInformerFacadeRegistration
{
    public static IServiceCollection AddSystemInformerFacade(this IServiceCollection services)
    {
        services.AddSingleton<SystemInformerView>();
        services.AddSingleton<SystemInformerFacade>();
        return services;
    }
}

public sealed class SystemInformerFacade
{
    private readonly SystemInformerView _view;

    public SystemInformerFacade(SystemInformerView view)
    {
        _view = view;
    }

    public Control Start()
    {
        _view.Start();
        return _view;
    }

    public Task StopAsync() => _view.StopAsync();
}

using Avalonia;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows10.0")]

namespace SystemInformer.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => AppBuilder.Configure<InformerApp>()
        .UsePlatformDetect()
        .LogToTrace()
        .StartWithClassicDesktopLifetime(args);
}

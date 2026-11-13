using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Edirstat.Client;

namespace Edirstat.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var scan = Array.IndexOf(args, "--scan");
        if (scan >= 0 && scan + 1 < args.Length) DiskApplication.StartupPath = args[scan + 1];
        AppBuilder.Configure<DiskApplication>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class DiskApplication : Application
{
    internal static string? StartupPath { get; set; }
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var view = new DiskUsageView();
            var window = new Window { Title = "BetterTaskManager · 磁盘空间", Width = 1320, Height = 860, MinWidth = 1040, MinHeight = 600, Content = view };
            view.ScanCompleted += path => window.Title = "BetterTaskManager · 磁盘空间 · " + path + " · 扫描完成";
            window.Opened += (_, _) => { if (StartupPath is { } path) view.StartScan(path); };
            var stopped = false;
            var closing = false;
            window.Closing += async (_, e) =>
            {
                if (stopped) return;
                e.Cancel = true;
                if (closing) return;
                closing = true;
                await view.StopAsync();
                stopped = true;
                window.Close();
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}

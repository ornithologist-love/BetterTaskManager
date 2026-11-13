using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Semi.Avalonia;
using Avalonia.Threading;
using SystemInformer.Client;
using SystemInformer.Client.Themes;
using SystemInformer.PluginFacade;

namespace SystemInformer.App;

public sealed class InformerApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://SystemInformer.App/"))
        {
            Source = new Uri("avares://Heron.ClientControls/Themes/ColorTokens.axaml")
        });
        Styles.Add(new SemiTheme());
        Styles.Add(new StyleInclude(new Uri("avares://SystemInformer.App/"))
        {
            Source = new Uri("avares://Semi.Avalonia.DataGrid/Index.axaml")
        });
        Styles.Add(new StyleInclude(new Uri("avares://SystemInformer.App/"))
        {
            Source = new Uri("avares://Heron.ClientControls/Themes/HeronTheme.axaml")
        });
        Styles.Add(MonitorTheme.Styles());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var view = new SystemInformerView { CpuDiagnostics = Console.WriteLine };
            var facade = new SystemInformerFacade(view);
            var window = new Window
            {
                Title = "BetterTaskManager · 任务管理器",
                Width = 1440, Height = 900, MinWidth = 980, MinHeight = 600,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                Content = view
            };
            MonitorTheme.Bind(window, Window.BackgroundProperty, "BgAppBrush");
            MonitorTheme.Bind(window, Window.ForegroundProperty, "FgDefaultBrush");
            var closing = false;
            var canClose = false;
            window.Opened += (_, _) =>
            {
                facade.Start();
                Console.WriteLine($"SystemInformer.App window opened · PID {Environment.ProcessId} · {Environment.ProcessPath}");
            };
            window.Closing += async (_, args) =>
            {
                if (canClose) return;
                args.Cancel = true;
                if (closing) return;
                closing = true;
                window.IsEnabled = false;
                try { await facade.StopAsync(); }
                catch (Exception error) { Console.Error.WriteLine(error); }
                finally
                {
                    canClose = true;
                    Dispatcher.UIThread.Post(() => window.Close());
                }
            };
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}

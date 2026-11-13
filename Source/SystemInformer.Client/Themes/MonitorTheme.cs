using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;

namespace SystemInformer.Client.Themes;

/// <summary>Consumes the host's semantic tokens; the standalone shell loads the same shared dictionary.</summary>
public static class MonitorTheme
{
    public static StyleInclude Styles() => new(new Uri("avares://SystemInformer.Client/"))
    {
        Source = new Uri("avares://SystemInformer.Client/Themes/InformerStyles.axaml")
    };

    public static void Bind(StyledElement target, AvaloniaProperty property, string key) =>
        target.Bind(property, new DynamicResourceExtension(key));

    public static IBrush Brush(Control target, string key) =>
        target.TryFindResource(key, target.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush : Brushes.Transparent;

    public static TextBlock Text(string role, string? text = null)
    {
        var block = new TextBlock { Text = text };
        block.Classes.Add(role);
        return block;
    }
}

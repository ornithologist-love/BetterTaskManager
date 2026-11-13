using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SystemInformer.Client.Themes;

namespace SystemInformer.Client.Performance;

internal sealed class PerformanceView : UserControl
{
    private readonly StackPanel _sidebar = new() { Spacing = 4 };
    private readonly Dictionary<string, SidebarItem> _items = new();
    private readonly TextBlock _title = MonitorTheme.Text("textPageTitle"), _subtitle = MonitorTheme.Text("textBody"),
        _legend = MonitorTheme.Text("textSecondary"), _axis = MonitorTheme.Text("textMeta");
    private readonly HistoryGraph _main = new() { MinHeight = 180 };
    private readonly ComboBox _range = new() { ItemsSource = new[] { "60 秒", "5 分钟" }, SelectedIndex = 0, MinWidth = 100 };
    private readonly CheckBox _showEngines = new() { Content = "每个逻辑 CPU 一张图" };
    private readonly WrapPanel _metrics = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<string, TextBlock> _metricValues = new();
    private readonly Dictionary<string, HistoryGraph> _engineGraphs = new();
    private readonly UniformGrid _engineGrid = new() { Columns = 4 };
    private readonly ScrollViewer _engineScroll;
    private readonly StackPanel _secondary = new() { Spacing = 4 };
    private readonly Dictionary<string, HistoryGraph> _secondaryGraphs = new();
    private readonly MemoryComposition _composition = new();
    private readonly StackPanel _compositionPanel = new() { Spacing = 3 };
    private PerformanceSnapshot? _snapshot;
    private string _selected = "cpu";
    private int Seconds => _range.SelectedIndex == 1 ? 300 : 60;

    public PerformanceView()
    {
        _subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _engineScroll = new() { Content = _engineGrid, IsVisible = false };
        var header = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 12 };
        header.Children.Add(_title);
        Grid.SetColumn(_subtitle, 1); _subtitle.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(_subtitle);
        Grid.SetColumn(_range, 2); header.Children.Add(_range);
        _compositionPanel.Children.Add(MonitorTheme.Text("textFieldLabel", "内存组合"));
        _compositionPanel.Children.Add(_composition);
        _compositionPanel.IsVisible = false;
        var graphArea = new Grid { MinHeight = 180 };
        graphArea.Children.Add(_main); graphArea.Children.Add(_engineScroll);
        var footer = new StackPanel { Spacing = 5 };
        footer.Children.Add(_showEngines); footer.Children.Add(_metrics);
        var body = new Grid { RowDefinitions = new("Auto,Auto,*,Auto,Auto,Auto,Auto"), RowSpacing = 6 };
        body.Children.Add(header);
        Grid.SetRow(_legend, 1); body.Children.Add(_legend);
        Grid.SetRow(graphArea, 2); body.Children.Add(graphArea);
        Grid.SetRow(_axis, 3); body.Children.Add(_axis);
        Grid.SetRow(_compositionPanel, 4); body.Children.Add(_compositionPanel);
        Grid.SetRow(_secondary, 5); body.Children.Add(_secondary);
        Grid.SetRow(footer, 6); body.Children.Add(footer);
        var root = new Grid { ColumnDefinitions = new("235,*"), ColumnSpacing = 16, Margin = new Thickness(0, 8, 0, 0) };
        var navigation = new Border { Child = new ScrollViewer { Content = _sidebar } };
        navigation.Classes.Add("monitorNavigation");
        root.Children.Add(navigation);
        var bodyScroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        root.SizeChanged += (_, _) => body.MinHeight = Math.Max(650, root.Bounds.Height - 26);
        var surface = new Border { Child = bodyScroll };
        surface.Classes.Add("monitorSurface");
        Grid.SetColumn(surface, 1); root.Children.Add(surface);
        Content = root;
        _title.Text = "CPU";
        ToolTip.SetTip(_title, "正在读取系统信息…");
        _range.SelectionChanged += (_, _) => Refresh();
        _showEngines.IsCheckedChanged += (_, _) => Refresh();
    }

    public void Apply(PerformanceSnapshot snapshot)
    {
        _snapshot = snapshot;
        var keys = snapshot.Sections.Select(section => section.Key).ToArray();
        foreach (var key in _items.Keys.Except(keys).ToArray()) _items.Remove(key);
        foreach (var section in snapshot.Sections)
        {
            if (!_items.TryGetValue(section.Key, out var item))
            {
                var key = section.Key;
                item = new();
                item.Button.Click += (_, _) => { _selected = key; _showEngines.IsChecked = false; Refresh(); };
                _items.Add(key, item);
            }
            item.Name.Text = section.Title;
            item.Subtitle.Text = section.Subtitle;
            item.Summary.Text = section.Summary;
            ToolTip.SetTip(item.Button, section.Subtitle.Length == 0 ? section.Status : section.Subtitle + "\n" + section.Status);
            item.Graph.Update(section.Main, section.Kind, snapshot.Time, Seconds, compact: true);
        }
        var buttons = keys.Select(key => _items[key].Button).ToArray();
        if (!_sidebar.Children.SequenceEqual(buttons))
        {
            _sidebar.Children.Clear();
            foreach (var button in buttons) _sidebar.Children.Add(button);
        }
        if (!keys.Contains(_selected)) _selected = keys.FirstOrDefault() ?? "cpu";
        Refresh();
    }

    private void Refresh()
    {
        if (_snapshot == null) return;
        var section = _snapshot.Sections.FirstOrDefault(section => section.Key == _selected);
        if (section == null) return;
        foreach (var pair in _items)
        {
            var selected = pair.Key == _selected;
            pair.Value.Button.IsChecked = selected;
            pair.Value.Graph.Update(_snapshot.Sections.First(section => section.Key == pair.Key).Main,
                _snapshot.Sections.First(section => section.Key == pair.Key).Kind, _snapshot.Time, Seconds, compact: true);
        }
        _title.Text = section.Title; _subtitle.Text = section.Subtitle;
        ToolTip.SetTip(_subtitle, section.Subtitle);
        _legend.Text = section.Main.Title + " · " + section.Main.FirstLabel
            + (section.Main.SecondLabel == null ? "" : " / " + section.Main.SecondLabel);
        ToolTip.SetTip(_title, section.Status);
        _axis.Text = $"{Seconds} 秒前 → 当前";
        _main.Update(section.Main, section.Kind, _snapshot.Time, Seconds);
        var engines = section.Extra.Where(series => section.Kind == ResourceKind.Cpu || series.Key.Contains("/engine/", StringComparison.Ordinal)).ToArray();
        _showEngines.IsVisible = engines.Length > 0;
        _showEngines.Content = section.Kind == ResourceKind.Cpu ? "每个逻辑 CPU 一张图" : "显示各引擎";
        var expanded = _showEngines.IsChecked == true && engines.Length > 0;
        _main.IsVisible = !expanded; _engineScroll.IsVisible = expanded;
        if (expanded) UpdateCharts(engines, _engineGraphs, _engineGrid, section.Kind, 130);
        var secondary = section.Extra.Where(series => section.Kind != ResourceKind.Cpu && !series.Key.Contains("/engine/", StringComparison.Ordinal)).ToArray();
        _secondary.IsVisible = secondary.Length > 0;
        UpdateCharts(secondary, _secondaryGraphs, _secondary, section.Kind, 110);
        _compositionPanel.IsVisible = section.Composition is { Count: > 0 };
        _composition.Update(section.Composition);
        if (section.Composition != null) ToolTip.SetTip(_compositionPanel, string.Join("\n", section.Composition.Select(part => $"{part.Label}：{ResourceFormat.Bytes(part.Bytes)}")));
        var metricKeys = section.Metrics.Select(metric => metric.Label).ToArray();
        if (!_metricValues.Keys.SequenceEqual(metricKeys))
        {
            _metrics.Children.Clear(); _metricValues.Clear();
            foreach (var metric in section.Metrics)
            {
                var value = MonitorTheme.Text("textBody");
                value.TextTrimming = TextTrimming.CharacterEllipsis; value.MaxWidth = 360;
                var cell = new StackPanel { Margin = new Thickness(0, 0, 20, 5), MinWidth = 110, Spacing = 1 };
                cell.Children.Add(MonitorTheme.Text("textFieldLabel", metric.Label));
                cell.Children.Add(value);
                _metrics.Children.Add(cell); _metricValues.Add(metric.Label, value);
            }
        }
        foreach (var metric in section.Metrics) { _metricValues[metric.Label].Text = metric.Value; ToolTip.SetTip(_metricValues[metric.Label], metric.Value); }
    }

    private void UpdateCharts(IReadOnlyList<PerformanceSeries> series, Dictionary<string, HistoryGraph> graphs, Panel panel,
        ResourceKind kind, double height)
    {
        if (!graphs.Keys.SequenceEqual(series.Select(item => item.Key)))
        {
            panel.Children.Clear(); graphs.Clear();
            foreach (var item in series)
            {
                var graph = new HistoryGraph { Height = height - 24 };
                var card = new StackPanel { Spacing = 3, Margin = panel == _engineGrid ? new Thickness(0, 0, 8, 8) : default };
                var title = MonitorTheme.Text("textFieldLabel", item.Title);
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                card.Children.Add(title);
                card.Children.Add(graph); panel.Children.Add(card); graphs.Add(item.Key, graph);
            }
        }
        foreach (var item in series) graphs[item.Key].Update(item, kind, _snapshot!.Time, Seconds);
    }

    private sealed class SidebarItem
    {
        public ToggleButton Button { get; } = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(7), BorderThickness = new Thickness(1) };
        public HistoryGraph Graph { get; } = new() { Width = 66, Height = 48, VerticalAlignment = VerticalAlignment.Center };
        public TextBlock Name { get; } = MonitorTheme.Text("textBody");
        public TextBlock Subtitle { get; } = MonitorTheme.Text("textMeta");
        public TextBlock Summary { get; } = MonitorTheme.Text("textSecondary");
        public SidebarItem()
        {
            Button.Classes.Add("monitorToggle"); Button.Classes.Add("monitorNavigation");
            Name.TextTrimming = Subtitle.TextTrimming = Summary.TextTrimming = TextTrimming.CharacterEllipsis;
            var row = new Grid { ColumnDefinitions = new("66,*"), ColumnSpacing = 9 };
            row.Children.Add(Graph);
            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Name); text.Children.Add(Subtitle); text.Children.Add(Summary);
            Grid.SetColumn(text, 1); row.Children.Add(text); Button.Content = row;
        }
    }
}

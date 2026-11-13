using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace Edirstat.Client;

public sealed partial class DiskUsageView : UserControl
{
    private readonly TextBox _path = new() { PlaceholderText = "扫描目录或盘符", MinWidth = 180, Height = 32 };
    private readonly ComboBox _drives = new() { Width = 185, Height = 32 };
    private readonly Button _browse = Button("选择目录");
    private readonly Button _start = Button("扫描");
    private readonly Button _cancel = Button("取消");
    private readonly Button _release = Button("释放结果");
    private readonly Button _up = Button("上一级");
    private readonly Button _home = Button("扫描根目录");
    private readonly Button _focus = Button("放大选中目录");
    private readonly Button _reveal = Button("在树中定位");
    private readonly Button _explorer = Button("资源管理器");
    private readonly Button _copy = Button("复制路径");
    private readonly TextBlock _status = Text("选择目录后扫描", 12);
    private readonly TextBlock _volumeName = Text("—", 12);
    private readonly TextBlock _volumeTotal = Text("—", 12);
    private readonly TextBlock _volumeUsed = Text("—", 12);
    private readonly TextBlock _volumeFree = Text("—", 12);
    private readonly TextBlock _volumeReserved = Text("—", 12);
    private VolumeSpace? _volumeSpace;
    private bool _updatingVolume;
    private string? _scanRootPath;
    private readonly TextBlock _location = Text("目录树 / 文件类型 / 文件分布", 12);
    private readonly TextBlock _selection = Text("单击文件联动目录树，双击矩形放大，右键打开菜单。", 12);
    private readonly TextBlock _hoverText = Text("", 11);
    private readonly TextBlock _fileSummary = Text("", 11);
    private readonly ProgressBar _progress = new() { Height = 4, Minimum = 0, Maximum = 100 };
    private readonly TextBox _nameFilter = new() { PlaceholderText = "筛选文件名，回车搜索", MaxLength = 256, MinWidth = 150, Height = 30 };
    private readonly Button _applyFilter = Button("筛选");
    private readonly Button _clearFilter = Button("清除筛选");
    private readonly DataGrid _tree = Grid();
    private readonly DataGrid _files = Grid();
    private readonly DataGrid _extensions = Grid();
    private readonly TabControl _tabs = new();
    private readonly DiskTreemap _treemap = new();
    private readonly FileTypeIcons _typeIcons = new();
    private readonly ObservableCollection<TreeRow> _rows = [];
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Stopwatch _scanClock = new();
    private ScanHandle? _scan;
    private SnapshotHandle? _snapshot;
    private TreeRow? _root;
    private NodeInfo? _selected;
    private string? _fileType;
    private uint _sort = 1;
    private bool _descending = true;
    private bool _busy, _stopping, _cancelRequested, _updating;
    private Task _work = Task.CompletedTask, _mapWork = Task.CompletedTask, _hoverWork = Task.CompletedTask;
    private int _mapGeneration, _hoverGeneration;
    private uint _hoverId;
    public event Action<string>? ScanCompleted;
    private readonly bool _useHostTheme;

    public DiskUsageView(bool useHostTheme = false)
    {
        _useHostTheme = useHostTheme;
        FontSize = 12;
        if (!useHostTheme)
        {
            Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Edirstat.Client/")) { Source = new Uri("avares://Edirstat.Client/Themes/StandaloneColors.axaml") });
            Styles.Add(new StyleInclude(new Uri("avares://Edirstat.Client/")) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml") });
        }
        Styles.Add(new StyleInclude(new Uri("avares://Edirstat.Client/")) { Source = new Uri("avares://Edirstat.Client/Themes/DiskUsageStyles.axaml") });
        _start.Classes.Add("buttonPrimary");
        _start.Classes.Remove("buttonSecondary");
        ActualThemeVariantChanged += (_, _) => UpdateTheme();
        AttachedToVisualTree += (_, _) => { UpdateTheme(); CheckResultRetention(); };
        DetachedFromVisualTree += (_, _) => CheckResultRetention();
        PropertyChanged += (_, change) => { if (change.Property == IsVisibleProperty) CheckResultRetention(); };
        _tree.ItemsSource = _rows;
        BuildColumns();
        _drives.Width = double.NaN; _drives.HorizontalAlignment = HorizontalAlignment.Stretch;
        var selector = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), ColumnSpacing = 6 };
        Add(selector, Text("选择：", 12), 0); Add(selector, _drives, 1); Add(selector, _start, 2); Add(selector, _cancel, 3);
        var scanPanel = new Grid { RowDefinitions = new("Auto,Auto,Auto"), RowSpacing = 6 };
        scanPanel.Children.Add(selector); AddRow(scanPanel, _status, 1); AddRow(scanPanel, _progress, 2);
        _progress.Height = 12;
        ThemeBind(_progress, ProgressBar.ForegroundProperty, "AccentBrush");
        ThemeBind(_progress, ProgressBar.BackgroundProperty, "BgDisabledBrush");
        var volumePanel = BuildVolumePanel();
        var header = new Grid { ColumnDefinitions = new("430,*,Auto"), ColumnSpacing = 14 };
        Add(header, scanPanel, 0); Add(header, volumePanel, 1);
        var headerBorder = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(9), Child = header };
        ThemeBind(headerBorder, Border.BackgroundProperty, "BgSurfaceSubtleBrush");
        ThemeBind(headerBorder, Border.BorderBrushProperty, "BorderDefaultBrush");
        var toolbar = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 6 };
        Add(toolbar, Text("扫描路径：", 12), 0); Add(toolbar, _path, 1); Add(toolbar, _browse, 2);
        var navigation = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 6 };
        Add(navigation, _up, 0); Add(navigation, _home, 1); Add(navigation, _location, 2);
        Add(navigation, _focus, 3); Add(navigation, _reveal, 4); Add(navigation, _explorer, 5); Add(navigation, _copy, 6);
        Add(navigation, _release, 7);
        ToolTip.SetTip(_release, "释放扫描结果及占用的内存；离开磁盘页五分钟后也会自动释放。再次查看需手动扫描。");
        var fileToolbar = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 6 };
        Add(fileToolbar, _nameFilter, 0); Add(fileToolbar, _applyFilter, 1); Add(fileToolbar, _clearFilter, 2);
        var filePanel = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 5 };
        filePanel.Children.Add(fileToolbar); AddRow(filePanel, _files, 1); AddRow(filePanel, _fileSummary, 2);
        _tabs.ItemsSource = new TabItem[] { new() { Header = "目录树", Content = _tree }, new() { Header = "大文件", Content = filePanel } };
        _tabs.SelectedIndex = 0;
        var typesPanel = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 5 };
        typesPanel.Children.Add(Text("文件类型 · 单击类型筛选并高亮", 12)); AddRow(typesPanel, _extensions, 1);
        var upper = new Grid { ColumnDefinitions = new("*,6,350"), ColumnSpacing = 6 };
        Add(upper, _tabs, 0); Add(upper, new GridSplitter { Width = 6, ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent }, 1); Add(upper, typesPanel, 2);
        var mapPanel = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 4 };
        mapPanel.Children.Add(_mapHeader); AddRow(mapPanel, _treemap, 1);
        var body = _body = new Grid { RowDefinitions = new("3*,6,2*"), MinHeight = 240 };
        body.Children.Add(upper);
        _mapSplitter = new GridSplitter { Height = 6, ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        AddRow(body, _mapSplitter, 1);
        AddRow(body, mapPanel, 2);
        var layout = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*,Auto,Auto"), RowSpacing = 7, Margin = new Thickness(10) };
        layout.Children.Add(headerBorder); AddRow(layout, toolbar, 1); AddRow(layout, navigation, 3);
        AddRow(layout, body, 4); AddRow(layout, _selection, 5); AddRow(layout, _hoverText, 6);
        Content = layout;
        LoadDrives();
        _path.Text = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        UpdateVolume(); UpdateButtons();
        _drives.SelectionChanged += (_, _) => { if (_drives.SelectedItem is DriveChoice drive && !_busy && !_updatingVolume) { _path.Text = drive.Root; UpdateVolume(); } };
        _browse.Click += async (_, _) => { try { if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return; var folders = await storage.OpenFolderPickerAsync(new() { Title = "选择扫描目录", AllowMultiple = false }); if (!_stopping && folders.Count > 0 && folders[0].Path.IsFile) { _path.Text = folders[0].Path.LocalPath; UpdateVolume(); } } catch (Exception e) { _status.Text = e.Message; } };
        _start.Click += (_, _) => Run(ScanAsync);
        _cancel.Click += (_, _) => Cancel();
        _release.Click += (_, _) => Run(() => ClearResultsAsync("扫描结果已释放，可重新扫描"));
        _path.KeyDown += (_, e) => { if (e.Key == Key.Enter) Run(ScanAsync); };
        _up.Click += (_, _) => ZoomOut();
        _home.Click += (_, _) => Run(() => FocusAsync(0));
        _focus.Click += (_, _) => { if (_selected is { } info) Run(() => FocusAsync(info.Entry.Directory ? info.Entry.Id : info.Entry.Parent)); };
        _reveal.Click += (_, _) => { if (_selected is { } info) Run(() => RevealAsync(info.Entry.Id)); };
        _explorer.Click += (_, _) => OpenExplorer();
        _copy.Click += async (_, _) => { if (_selected is { } info && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) { try { await clipboard.SetTextAsync(info.Path); } catch (Exception e) { _status.Text = e.Message; } } };
        _tree.SelectionChanged += (_, _) => { if (!_updating && _tree.SelectedItem is TreeRow { More: false } row) Run(() => SelectAsync(row.Entry.Id)); };
        _tree.DoubleTapped += (_, _) => { if (_tree.SelectedItem is TreeRow row) Run(() => ToggleAsync(row)); };
        _tree.Sorting += (_, e) => { e.Handled = true; if (uint.TryParse(e.Column.SortMemberPath, out var sort)) Run(async () => { _descending = _sort == sort ? !_descending : sort != 0; _sort = sort; if (_root is { } root) { root.Children.Clear(); root.Loaded = false; await LoadChildrenAsync(root, 0); UpdateTree(); _status.Text = "目录树已按 " + e.Column.Header + (_descending ? " 降序排列" : " 升序排列"); } }); };
        _files.SelectionChanged += (_, _) => { if (!_updating && _files.SelectedItem is DiskEntry entry) Run(() => SelectAsync(entry.Id)); };
        _files.DoubleTapped += (_, _) => { if (_files.SelectedItem is DiskEntry entry) Run(() => RevealAsync(entry.Id)); };
        _extensions.SelectionChanged += (_, _) => { if (_updating || _busy) return; _fileType = (_extensions.SelectedItem as ExtensionInfo)?.Extension; _treemap.Filter(_fileType); _tabs.SelectedIndex = 1; Run(RefreshFilesAsync); };
        _applyFilter.Click += (_, _) => Run(RefreshFilesAsync);
        _nameFilter.KeyDown += (_, e) => { if (e.Key == Key.Enter) Run(RefreshFilesAsync); };
        _clearFilter.Click += (_, _) => Run(async () => { _updating = true; _extensions.SelectedItem = null; _updating = false; _fileType = null; _nameFilter.Text = ""; _treemap.Filter(null); await RefreshFilesAsync(); });
        _treemap.Selected += id => { if (id == uint.MaxValue) { _selected = null; _mapTarget = null; _selection.Text = "可用空间"; UpdateButtons(); } else Run(() => RevealAsync(id)); };
        _treemap.ZoomRequested += id => Run(async () => { if (_snapshot is not { } snapshot) return; var info = await Task.Run(() => NativeScanner.Node(snapshot, id)); await FocusAsync(info.Entry.Directory ? id : info.Entry.Parent); if (!info.Entry.Directory) await RevealAsync(id); });
        BuildMapContextMenu();
        _treemap.SizeChanged += (_, _) => { _resizeTimer.Stop(); if (!_stopping) _resizeTimer.Start(); };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); QueueMap(); };
        _treemap.Hovered += id => { _hoverId = id; _hoverTimer.Stop(); if (!_stopping) _hoverTimer.Start(); };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); QueueHover(); };
        AddContextMenu(_tree); AddContextMenu(_files);
        _retentionTimer.Tick += (_, _) => CheckResultRetention();
    }

    private static Button Button(string text) { var button = new Button { Content = text, Height = 30, Padding = new Thickness(9, 2) }; button.Classes.Add("buttonSecondary"); return button; }
    private static void ThemeBind(StyledElement target, AvaloniaProperty property, string key) => target.Bind(property, new DynamicResourceExtension(key));
    private void UpdateTheme()
    {
        foreach (var grid in new[] { _tree, _files, _extensions })
            foreach (var mapping in new[] { ("DataGridRowPointeroverBackground", "BgHoverBrush"), ("DataGridRowSelectedBackground", "BgSelectedBrush"),
                ("DataGridRowSelectedPointeroverBackground", "BgSelectedBrush"), ("DataGridCellCurrentBorderBrush", "BorderFocusBrush"), ("DataGridCellFocusBorderBrush", "BorderFocusBrush") })
                if (this.TryFindResource(mapping.Item2, ActualThemeVariant, out var resource)) grid.Resources[mapping.Item1] = resource;
        _treemap.InvalidateVisual();
    }
    private static TextBlock Text(string text, double size) => new() { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private static DataGrid Grid() => new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserResizeColumns = true, CanUserSortColumns = true, RowHeight = 27, ColumnHeaderHeight = 29, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, SelectionMode = DataGridSelectionMode.Single, FontSize = 12 };
    private static void Add(Grid grid, Control control, int column) { Avalonia.Controls.Grid.SetColumn(control, column); grid.Children.Add(control); }
    private static void AddRow(Grid grid, Control control, int row) { Avalonia.Controls.Grid.SetRow(control, row); grid.Children.Add(control); }
    private DataGridColumn Column(string title, string property, double width, string? sort = null, string? deletion = null)
    {
        if (deletion is null) return new DataGridTextColumn { Header = title, Binding = new Binding(property), Width = new DataGridLength(width), SortMemberPath = sort ?? property };
        return new DataGridTemplateColumn { Header = title, Width = new DataGridLength(width), SortMemberPath = sort ?? property,
            CellTemplate = new FuncDataTemplate<object>((_, _) => DeletedText(property, deletion)) };
    }
    private TextBlock DeletedText(string property, string deletion)
    {
        var text = Text("", 12);
        text.Bind(TextBlock.TextProperty, new Binding(property));
        text.Bind(TextBlock.ForegroundProperty, new MultiBinding
        {
            Bindings = { new Binding(deletion + "IsDeleted"), new Binding(nameof(Foreground)) { Source = this } },
            Converter = new FuncMultiValueConverter<object, object>(values => values.First() is true ? Brushes.Red : values.Last() ?? AvaloniaProperty.UnsetValue)
        });
        text.Bind(TextBlock.TextDecorationsProperty, new Binding(deletion + "IsDeleted") { Converter = new FuncValueConverter<bool, TextDecorationCollection?>(deleted => deleted ? TextDecorations.Strikethrough : null) });
        return text;
    }
    private void BuildColumns()
    {
        _tree.Columns.Add(new DataGridTemplateColumn { Header = "文件夹 / 文件", Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 200, SortMemberPath = "0", CellTemplate = new FuncDataTemplate<TreeRow>((row, _) =>
        {
            if (row is null) return null;
            var cell = new Grid { ColumnDefinitions = new("20,20,*"), Margin = row.Indent };
            var expand = new Button { Padding = new Thickness(0), Width = 20, Height = 23, Background = Brushes.Transparent, BorderThickness = new Thickness(0), IsVisible = row.CanExpand };
            expand.Bind(ContentControl.ContentProperty, new Binding(nameof(TreeRow.Arrow)));
            expand.Click += (_, e) => { e.Handled = true; Run(() => ToggleAsync(row)); };
            cell.Children.Add(expand);
            Add(cell, new FluentIcon { Icon = row.Entry.Directory ? Icon.Folder : Icon.Document, IconSize = IconSize.Size16, IconVariant = IconVariant.Filled, Foreground = row.Entry.Directory ? new SolidColorBrush(Color.Parse("#C29232")) : Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, IsVisible = !row.More }, 1);
            Add(cell, DeletedText(nameof(TreeRow.Name), "Entry."), 2);
            return cell;
        }) });
        _tree.Columns.Add(Column("大小", "Size", 100, "1", "Entry."));
        _tree.Columns.Add(Column("占父级", "Percentage", 75, "1", "Entry."));
        _tree.Columns.Add(Column("文件", "Files", 80, "2", "Entry."));
        _tree.Columns.Add(Column("子目录", "Directories", 80, "3", "Entry."));
        _tree.Columns.Add(Column("最近修改", "Modified", 132, "4", "Entry."));
        _tree.Columns.Add(Column("状态", "Entry.Attributes", 70, "0", "Entry."));
        _files.Columns.Add(Column("文件名", "Name", 220, deletion: ""));
        _files.Columns.Add(Column("大小", "SizeText", 105, "Size", ""));
        _files.Columns.Add(Column("扩展名", "Extension", 90, deletion: ""));
        _files.Columns.Add(Column("修改时间", "ModifiedText", 132, "Modified", ""));
        _files.Columns.Add(new DataGridTemplateColumn { Header = "完整路径", SortMemberPath = "Path", CellTemplate = new FuncDataTemplate<object>((_, _) => DeletedText("Path", "")), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 260 });
        _extensions.Columns.Add(new DataGridTemplateColumn { Header = "", Width = new DataGridLength(22), CanUserSort = false, CellTemplate = new FuncDataTemplate<ExtensionInfo>((entry, _) =>
        {
            var swatch = new Border { Width = 13, Height = 18, Background = entry?.Brush, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            swatch.Bind(IsVisibleProperty, new Binding("!HasIcon"));
            var icon = new Image { Width = 18, Height = 18, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            icon.Bind(Image.SourceProperty, new Binding(nameof(ExtensionInfo.Icon)));
            icon.Bind(IsVisibleProperty, new Binding(nameof(ExtensionInfo.HasIcon)));
            return new Grid { Children = { swatch, icon } };
        }) });
        _extensions.Columns.Add(Column("类型", "Name", 92, "Extension"));
        _extensions.Columns.Add(Column("大小", "SizeText", 90, "Size"));
        _extensions.Columns.Add(Column("占比", "PercentageText", 65, "Percentage"));
        _extensions.Columns.Add(Column("文件", "FilesText", 76, "Files"));
    }
    private void AddContextMenu(DataGrid control)
    {
        NodeInfo? target = null;
        var reveal = new MenuItem { Header = "在目录树中定位" }; reveal.Click += (_, _) => { if (target is { } info) Run(() => RevealAsync(info.Entry.Id)); };
        var explorer = new MenuItem { Header = "浏览文件夹" }; explorer.Click += (_, _) => { if (target is { } info) BrowseTarget(info); };
        var copy = new MenuItem { Header = "复制路径" }; copy.Click += (_, _) => { if (target is { } info) Run(() => CopyTextAsync(info.Path)); };
        var delete = new MenuItem { Header = "删除…", InputGesture = new KeyGesture(Key.Delete) }; delete.Click += (_, _) => { if (target is { } info) Run(() => DeleteTargetAsync(info)); };
        var menu = new ContextMenu { ItemsSource = new Control[] { reveal, explorer, copy, new Separator(), delete } };
        control.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            if (_busy || _stopping) return;
            object? item = control.SelectedItem;
            if (e.TryGetPosition(control, out var point))
            {
                var hit = control.InputHitTest(point) as Visual;
                var row = hit as DataGridRow ?? hit?.FindAncestorOfType<DataGridRow>();
                item = row?.DataContext;
            }
            _updating = true; control.SelectedItem = item; _updating = false;
            var id = EntryId(item);
            Run(async () =>
            {
                target = null;
                if (id is { } node && _snapshot is not null) { await SelectAsync(node); target = _selected; }
                if (_stopping) return;
                reveal.IsEnabled = explorer.IsEnabled = copy.IsEnabled = target is not null;
                delete.IsEnabled = CanDelete(target);
                menu.Open(control);
            });
        };
        control.KeyDown += (_, e) => { if (e.Key == Key.Delete && EntryId(control.SelectedItem) is { } id) { e.Handled = true; DeleteById(id); } };
    }
    private static uint? EntryId(object? item) => item switch { TreeRow { More: false } row => row.Entry.Id, DiskEntry file => file.Id, _ => null };
    private sealed record DriveChoice(string Root, string Label) { public override string ToString() => Label; }
    private void LoadDrives()
    {
        var choices = new List<DriveChoice>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable))
            try { if (drive.IsReady) choices.Add(new(drive.Name, $"[{drive.Name.TrimEnd('\\')}] {drive.VolumeLabel}")); } catch (IOException) { }
        _drives.ItemsSource = choices;
    }
    private void UpdateVolume()
    {
        try
        {
            _volumeSpace = WindowsDesktop.Volume(_path.Text ?? "");
            _updatingVolume = true;
            try { _drives.SelectedItem = (_drives.ItemsSource as List<DriveChoice>)?.FirstOrDefault(d => string.Equals(d.Root, _volumeSpace.Root, StringComparison.OrdinalIgnoreCase)); }
            finally { _updatingVolume = false; }
            _volumeName.Text = _volumeSpace.Name;
            _volumeTotal.Text = VolumeSpace.Format(_volumeSpace.Total);
            _volumeUsed.Text = $"{VolumeSpace.Format(_volumeSpace.Used)}  ({_volumeSpace.UsedPercent:0.0}%)";
            _volumeFree.Text = $"{VolumeSpace.Format(_volumeSpace.Free)}  ({_volumeSpace.FreePercent:0.0}%)";
            _volumeReserved.Text = _volumeSpace.Reserved is { } reserved ? VolumeSpace.Format(reserved) : "不可用";
            if (!_scanClock.IsRunning) _progress.Value = _snapshot is not null ? 100 : 0;
        }
        catch (Exception) { _volumeSpace = null; _volumeName.Text = _volumeTotal.Text = _volumeUsed.Text = _volumeFree.Text = _volumeReserved.Text = "—"; }
    }
    private void Run(Func<Task> action)
    {
        if (_busy || _stopping) return;
        _busy = true; UpdateButtons(); _work = RunAsync(action);
    }
    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (DllNotFoundException) { _status.Text = "缺少扫描后端 DLL，请使用完整独立应用包。"; }
        catch (Exception e) { _status.Text = $"操作失败：{e.Message}"; }
        finally { _busy = false; UpdateButtons(); }
    }
    private void UpdateButtons()
    {
        _start.IsEnabled = _browse.IsEnabled = _path.IsEnabled = _drives.IsEnabled = !_busy && !_stopping;
        _release.IsEnabled = !_busy && !_stopping && _snapshot is not null;
        _up.IsEnabled = !_busy && !_stopping && _root is { Entry.Parent: not uint.MaxValue };
        _home.IsEnabled = !_busy && !_stopping && _root is { Entry.Id: not 0 };
        _focus.IsEnabled = _reveal.IsEnabled = _explorer.IsEnabled = _copy.IsEnabled = !_busy && !_stopping && _selected is not null;
        _tree.IsEnabled = _files.IsEnabled = _extensions.IsEnabled = _applyFilter.IsEnabled = _clearFilter.IsEnabled = !_busy && !_stopping && _snapshot is not null;
        _cancel.IsEnabled = !_stopping && (_scanClock.IsRunning || _allocationRunning) && !_cancelRequested;
    }
    public void StartScan(string path) { _path.Text = path; Run(ScanAsync); }
    private async Task ScanAsync()
    {
        var path = Path.GetFullPath((_path.Text ?? "").Trim());
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("目录不存在或无法访问");
        _path.Text = path; await ReleaseSnapshotAsync(); _scanRootPath = path; UpdateVolume();
        _cancelRequested = false; _scanClock.Restart(); _progress.IsIndeterminate = true; UpdateButtons();
        _status.Text = "正在扫描…";
        try
        {
            _scan = await Task.Run(() => NativeScanner.Start(path));
            if (_stopping || _cancelRequested) NativeScanner.Cancel(_scan);
            ScanProgress progress;
            do
            {
                var scan = _scan;
                progress = await Task.Run(() => NativeScanner.Progress(scan));
                _status.Text = $"{(_cancelRequested ? "正在取消" : progress.Done ? "扫描完成" : "扫描中")} {_scanClock.Elapsed.TotalSeconds:0.00} 秒 · {progress.Files:N0} 文件 · {progress.Directories:N0} 目录 · {DiskSize.Format(progress.Bytes)}";
                if (!progress.Done) await Task.Delay(250);
            } while (!progress.Done);
            _scanClock.Stop();
            if (progress.Cancelled || _stopping) { _status.Text = "扫描已取消"; return; }
            var completed = _scan;
            _snapshot = await Task.Run(() => NativeScanner.Snapshot(completed));
            _retentionTimer.Start();
            if (_allocatedMenu.IsChecked) await SetAllocatedAsync(true);
            await FocusAsync(0);
            await _mapWork;
            if (!_stopping) ScanCompleted?.Invoke(path);
        }
        finally
        {
            _scanClock.Stop(); _progress.IsIndeterminate = false; UpdateVolume();
            var scan = _scan; _scan = null; if (scan is not null) await Task.Run(scan.Dispose);
        }
    }
    private async Task FocusAsync(uint id)
    {
        if (_snapshot is not { } snapshot || _stopping) return;
        _mapGeneration++; _hoverGeneration++; _selected = null;
        _treemap.SetImage(null);
        var info = await Task.Run(() => NativeScanner.Node(snapshot, id, _sort, _descending));
        if (!info.Entry.Directory) return;
        _root = new(TrackDeletion(info.Entry with { Path = info.Path }), 0) { Expanded = true };
        _location.Text = info.Path + " · " + info.Entry.SizeText;
        await LoadChildrenAsync(_root, 0); UpdateTree(); QueueMap();
        var types = await Task.Run(() => NativeScanner.Extensions(snapshot, id));
        foreach (var type in types) type.Percentage = info.Entry.Size == 0 ? 0 : 100.0 * type.Size / info.Entry.Size;
        _updating = true; _extensions.ItemsSource = types; _extensions.SelectedItem = null; _updating = false;
        _typeIcons.Request(types);
        _fileType = null; _nameFilter.Text = ""; _treemap.Filter(null);
        await RefreshFilesAsync();
        await SelectAsync(id);
    }
    private async Task LoadChildrenAsync(TreeRow owner, uint offset)
    {
        if (_snapshot is not { } snapshot) return;
        var page = await Task.Run(() => NativeScanner.Page(snapshot, owner.Entry.Id, offset, _sort, _descending));
        var items = page.Items.Select(entry => new TreeRow(TrackDeletion(entry with { Path = Path.Combine(page.Path, entry.Name) }), owner.Depth + 1, owner)).ToArray();
        if (!owner.Loaded) { owner.Children.Clear(); owner.StartOffset = page.Offset; owner.NextOffset = page.Offset; }
        if (page.Offset < owner.StartOffset) { owner.Children.InsertRange(0, items); owner.StartOffset = page.Offset; }
        else owner.Children.AddRange(items);
        owner.NextOffset = Math.Max(owner.NextOffset, page.Offset + (uint)items.Length);
        owner.Total = page.Total; owner.Loaded = true;
    }
    private async Task ToggleAsync(TreeRow row)
    {
        if (row.More && row.Owner is { } owner) { await LoadChildrenAsync(owner, row.NextOffset); UpdateTree(); return; }
        if (!row.Entry.Directory) return;
        if (!row.Loaded) await LoadChildrenAsync(row, 0);
        row.Expanded = !row.Expanded; UpdateTree(row.Entry.Id);
    }
    private void UpdateTree(uint? selected = null)
    {
        _updating = true; _rows.Clear();
        if (_root is { } root) Flatten(root);
        if (selected is { } id) _tree.SelectedItem = _rows.FirstOrDefault(r => !r.More && r.Entry.Id == id);
        _updating = false;
    }
    private void Flatten(TreeRow row)
    {
        _rows.Add(row); if (!row.Expanded) return;
        if (row.StartOffset > 0) _rows.Add(new TreeRow(row.Entry, row.Depth + 1, row, true) { NextOffset = row.StartOffset >= 512 ? row.StartOffset - 512 : 0 });
        foreach (var child in row.Children) Flatten(child);
        if (row.NextOffset < row.Total) _rows.Add(new TreeRow(row.Entry, row.Depth + 1, row, true) { NextOffset = row.NextOffset });
    }
    private async Task SelectAsync(uint id)
    {
        if (_snapshot is not { } snapshot) return;
        _selected = await Task.Run(() => NativeScanner.Node(snapshot, id, uint.MaxValue));
        _selection.Text = $"{_selected.Path} · {SelectedSize(_selected.Entry)} · {_selected.Entry.FilesText} 文件{(_selected.Entry.Denied ? " · 无权限" : "")}{(IsDeleted(_selected) ? " · 已删除（扫描时数据）" : "")}";
        _treemap.Highlight(id);
    }
    private async Task RevealAsync(uint id)
    {
        if (_snapshot is not { } snapshot || _root is not { } root) return;
        var info = await Task.Run(() => NativeScanner.Node(snapshot, id, _sort, _descending));
        if (id == root.Entry.Id) { UpdateTree(id); await SelectAsync(id); return; }
        var chain = info.Ancestors.SkipWhile(parent => parent != root.Entry.Id).Skip(1).Append(id).ToArray();
        if (!info.Ancestors.Contains(root.Entry.Id)) { await FocusAsync(0); await RevealAsync(id); return; }
        var owner = root;
        foreach (var childId in chain)
        {
            owner.Expanded = true;
            if (!owner.Loaded) await LoadChildrenAsync(owner, 0);
            var child = owner.Children.FirstOrDefault(row => row.Entry.Id == childId);
            if (child is null)
            {
                var childInfo = childId == id ? info : await Task.Run(() => NativeScanner.Node(snapshot, childId, _sort, _descending));
                // Locate a bounded page directly, rather than loading millions of siblings.
                if (owner.Children.Count > 0) { owner.Children.Clear(); owner.Loaded = false; }
                await LoadChildrenAsync(owner, childInfo.Offset / 512 * 512);
                child = owner.Children.FirstOrDefault(row => row.Entry.Id == childId);
            }
            if (child is null) break;
            owner = child;
        }
        UpdateTree(id); _tabs.SelectedIndex = 0;
        if (_tree.SelectedItem is { } selected) _tree.ScrollIntoView(selected, null);
        await SelectAsync(id);
    }
    private async Task RefreshFilesAsync()
    {
        if (_snapshot is not { } snapshot || _root is not { } root) return;
        var filter = _nameFilter.Text ?? "";
        var type = _fileType;
        var page = await Task.Run(() => NativeScanner.Files(snapshot, root.Entry.Id, type, filter));
        foreach (var entry in page.Items) TrackDeletion(entry);
        _updating = true; _files.ItemsSource = page.Items; _updating = false;
        _fileSummary.Text = $"匹配 {page.Matched:N0} 个文件 · 显示最大的 {page.Items.Length:N0} 个{(type is null ? "" : " · 类型 " + (type.Length == 0 ? "无扩展名" : type))}";
    }
    private void ZoomOut() { if (_root is { Entry.Parent: not uint.MaxValue } root) Run(() => FocusAsync(root.Entry.Parent)); }
    private void QueueMap()
    {
        if (_stopping || !_showMapMenu.IsChecked || _snapshot is not { } snapshot || _root is not { } root) return;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = Math.Clamp((int)Math.Ceiling(_treemap.Bounds.Width * scale), 1, 6000);
        var height = Math.Clamp((int)Math.Ceiling(_treemap.Bounds.Height * scale), 1, 4000);
        if (width < 20 || height < 20) return;
        var generation = ++_mapGeneration;
        var free = _freeMenu.IsChecked && root.Entry.Id == 0 && IsWholeVolumeScan() ? _volumeSpace?.Free ?? 0 : 0;
        var used = free > 0 && _volumeSpace is { } volume ? volume.Total - Math.Min(volume.Total, volume.Free) : 0;
        _mapWork = RenderMapAsync(_mapWork, generation, snapshot, root.Entry.Id, width, height, scale, _useAllocated, used, free, _namesMenu.IsChecked);
    }
    private async Task RenderMapAsync(Task previous, int generation, SnapshotHandle snapshot, uint id, int width, int height, double scale, bool allocated, ulong used, ulong free, bool names)
    {
        await previous;
        if (_stopping || generation != _mapGeneration || snapshot != _snapshot) return;
        try
        {
            var image = await Task.Run(() => DiskTreemap.Rasterize(NativeScanner.Map(snapshot, id, width, height, scale, allocated, used, free, names), width, height));
            if (!_stopping && generation == _mapGeneration && snapshot == _snapshot) { _treemap.SetImage(image); UpdateDeletedMap(); }
        }
        catch (Exception e) { if (!_stopping && generation == _mapGeneration) _status.Text = "绘图失败：" + e.Message; }
    }
    private void QueueHover()
    {
        if (_stopping || _busy || _snapshot is not { } snapshot) return;
        if (_hoverId == uint.MaxValue) { _hoverGeneration++; _hoverText.Text = "可用空间"; return; }
        var generation = ++_hoverGeneration; var id = _hoverId;
        _hoverWork = HoverAsync(_hoverWork, generation, snapshot, id);
    }
    private async Task HoverAsync(Task previous, int generation, SnapshotHandle snapshot, uint id)
    {
        await previous;
        if (_stopping || generation != _hoverGeneration || snapshot != _snapshot) return;
        try { var info = await Task.Run(() => NativeScanner.Node(snapshot, id, uint.MaxValue)); if (!_stopping && generation == _hoverGeneration && snapshot == _snapshot) { _hoverText.Text = info.Path + " · " + SelectedSize(info.Entry); ToolTip.SetTip(_treemap, info.Path + "\n" + SelectedSize(info.Entry) + (info.Entry.Directory ? "\n双击放大" : "")); } }
        catch (Exception) { }
    }
    private void OpenExplorer()
    {
        if (_selected is not { } info) return;
        try { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; if (!info.Entry.Directory) start.ArgumentList.Add("/select,"); start.ArgumentList.Add(info.Path); Process.Start(start); }
        catch (Exception e) { _status.Text = e.Message; }
    }
    private void Cancel() { _cancelRequested = true; if (_scan is not null) NativeScanner.Cancel(_scan); if (_allocationRunning && _snapshot is { } snapshot) NativeScanner.CancelAllocation(snapshot); _cancel.IsEnabled = false; if (_scanClock.IsRunning || _allocationRunning) _status.Text = "正在取消…"; }
    private async Task ReleaseSnapshotAsync()
    {
        _retentionTimer.Stop(); _hiddenClock.Reset();
        _mapGeneration++; _hoverGeneration++; _resizeTimer.Stop(); _hoverTimer.Stop();
        await _mapWork; await _hoverWork;
        _treemap.SetImage(null);
        var snapshot = _snapshot; _snapshot = null; if (snapshot is not null) await Task.Run(snapshot.Dispose);
        _updating = true;
        try
        {
            _root = null; _selected = _mapTarget = null; _scanRootPath = null;
            _rows.Clear(); _files.ItemsSource = _extensions.ItemsSource = null;
            _deleted.Clear(); _treemap.SetDeleted([]);
            _fileType = null; _nameFilter.Text = "";
        }
        finally { _updating = false; }
        _useAllocated = false; _allocationFailures = 0; UpdateMapHeader();
        _fileSummary.Text = _hoverText.Text = "";
        _location.Text = "目录树 / 文件类型 / 文件分布";
        _selection.Text = "单击文件联动目录树，双击矩形放大，右键打开菜单。";
        ToolTip.SetTip(_treemap, null);
        _progress.Value = 0;
    }
    public async Task StopAsync()
    {
        _stopping = true; Cancel(); _deleteDialog?.Close(false); _mapMenu.Close(); _resizeTimer.Stop(); _hoverTimer.Stop(); UpdateButtons();
        await _work; await ReleaseSnapshotAsync(); _treemap.Dispose(); _typeIcons.Dispose();
    }
}

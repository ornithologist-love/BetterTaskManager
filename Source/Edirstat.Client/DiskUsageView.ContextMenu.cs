using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

namespace Edirstat.Client;

public sealed partial class DiskUsageView
{
    private readonly TextBlock _mapHeader = Text("文件分布 · 双击放大 / 右键菜单", 11);
    private readonly ContextMenu _mapMenu = new();
    private readonly MenuItem _showMapMenu = CheckMenu("显示树图", true);
    private readonly MenuItem _allocatedMenu = CheckMenu("在树图上显示分配大小", false);
    private readonly MenuItem _freeMenu = CheckMenu("在树图上显示可用空间", false);
    private readonly MenuItem _namesMenu = CheckMenu("在树图上显示文件夹和文件名称", true);
    private MenuItem _mapBrowse = null!, _mapCopy = null!, _mapCopySize = null!, _mapZoom = null!, _mapZoomOut = null!, _mapDelete = null!;
    private Grid _body = null!;
    private GridSplitter _mapSplitter = null!;
    private GridLength _mapHeight = new(2, GridUnitType.Star);
    private NodeInfo? _mapTarget;
    private Window? _deleteDialog;
    private readonly Dictionary<uint, NodeInfo> _deleted = [];
    private bool _useAllocated, _allocationRunning;
    private ulong _allocationFailures;
    private static MenuItem CheckMenu(string title, bool value) => new() { Header = title, ToggleType = MenuItemToggleType.CheckBox, IsChecked = value };

    private Grid BuildVolumePanel()
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,Auto,Auto,*"), RowDefinitions = new("Auto,Auto,Auto,Auto"), ColumnSpacing = 14, RowSpacing = 3 };
        void Cell(Control control, int row, int column) { Avalonia.Controls.Grid.SetRow(control, row); Add(grid, control, column); }
        Cell(Text("选择：", 12), 0, 0); Cell(_volumeName, 0, 1); Avalonia.Controls.Grid.SetColumnSpan(_volumeName, 3);
        Cell(Text("总空间：", 12), 1, 0); Cell(_volumeTotal, 1, 1);
        Cell(Text("已用空间：", 12), 2, 0); Cell(_volumeUsed, 2, 1);
        Cell(Text("可用空间：", 12), 3, 0); Cell(_volumeFree, 3, 1);
        Cell(Text("预留空间：", 12), 2, 2); Cell(_volumeReserved, 2, 3);
        foreach (var value in new[] { _volumeName, _volumeTotal, _volumeUsed, _volumeFree, _volumeReserved }) value.FontWeight = FontWeight.Bold;
        return grid;
    }

    private void BuildMapContextMenu()
    {
        _mapBrowse = new() { Header = "浏览文件夹" };
        _mapCopy = new() { Header = "复制路径" };
        _mapCopySize = new() { Header = "将文件和大小信息复制到剪贴板" };
        _mapZoom = new() { Header = "放大树图" };
        _mapZoomOut = new() { Header = "缩小树图" };
        _mapDelete = new() { Header = "删除…", InputGesture = new KeyGesture(Key.Delete) };
        _mapMenu.ItemsSource = new Control[] { _mapBrowse, _mapCopy, _mapCopySize, new Separator(),
            _mapZoom, _mapZoomOut, new Separator(), _showMapMenu, _allocatedMenu, _freeMenu, _namesMenu,
            new Separator(), _mapDelete };
        _treemap.MenuRequested += ShowMapMenu;
        _treemap.KeyDown += (_, e) => { if (e.Key == Key.Delete && _treemap.SelectedNode is { } id) { e.Handled = true; DeleteById(id); } };
        _mapHeader.Focusable = true;
        _mapHeader.ContextRequested += (_, e) => { e.Handled = true; ShowMapMenu(null); };
        _mapBrowse.Click += (_, _) => { if (_mapTarget is { } target) BrowseTarget(target); };
        _mapCopy.Click += (_, _) => { if (_mapTarget is { } target) Run(() => CopyTextAsync(target.Path)); };
        _mapCopySize.Click += (_, _) => { if (_mapTarget is { } target) Run(() => CopyTextAsync(FileSizeText(target))); };
        _mapZoom.Click += (_, _) => { if (_mapTarget is { } target) Run(() => FocusAsync(target.Entry.Directory ? target.Entry.Id : target.Entry.Parent)); };
        _mapZoomOut.Click += (_, _) => ZoomOut();
        _showMapMenu.Click += (_, _) => SetMapVisible(_showMapMenu.IsChecked);
        _allocatedMenu.Click += (_, _) => Run(() => SetAllocatedAsync(_allocatedMenu.IsChecked));
        _freeMenu.Click += (_, _) => QueueMap();
        _namesMenu.Click += (_, _) => { _treemap.ShowNames = _namesMenu.IsChecked; QueueMap(); _treemap.InvalidateVisual(); };
        _mapDelete.Click += (_, _) => { if (_mapTarget is { } target) Run(() => DeleteTargetAsync(target)); };
        ToolTip.SetTip(_freeMenu, "仅在扫描整卷、查看扫描根目录时显示可用空间。");
    }
    private void ShowMapMenu(uint? id)
    {
        Run(async () =>
        {
            _mapTarget = null;
            if (id is { } node && node != uint.MaxValue && _snapshot is not null) { await SelectAsync(node); _mapTarget = _selected; }
            if (_stopping) return;
            _mapBrowse.IsEnabled = _mapCopy.IsEnabled = _mapCopySize.IsEnabled = _mapZoom.IsEnabled = _mapTarget is not null;
            _mapDelete.IsEnabled = CanDelete(_mapTarget);
            _mapZoomOut.IsEnabled = _root is { Entry.Parent: not uint.MaxValue };
            _allocatedMenu.IsEnabled = _snapshot is not null;
            _freeMenu.IsEnabled = _root is { Entry.Id: 0 } && IsWholeVolumeScan();
            _mapMenu.Open(_treemap.IsVisible ? _treemap : _mapHeader);
        });
    }
    private void SetMapVisible(bool visible)
    {
        _mapGeneration++;
        if (!visible && _treemap.IsVisible) _mapHeight = _body.RowDefinitions[2].Height;
        _treemap.IsVisible = _mapSplitter.IsVisible = visible;
        _body.RowDefinitions[1].Height = visible ? new GridLength(6) : new GridLength(0);
        _body.RowDefinitions[2].Height = visible ? _mapHeight : GridLength.Auto;
        UpdateMapHeader();
        if (visible) QueueMap();
        else _treemap.SetImage(null);
    }
    private void UpdateMapHeader() => _mapHeader.Text = !_showMapMenu.IsChecked ? "文件分布（已隐藏）· 右键显示树图" :
        $"文件分布 · {(_useAllocated ? "分配大小" : "文件大小")} · 双击放大 / 右键菜单{(_allocationFailures > 0 && _useAllocated ? $" · {_allocationFailures:N0} 个文件的分配大小未读取" : "")}";
    private bool IsWholeVolumeScan() => _scanRootPath is { } root && _volumeSpace is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root) ?? ""), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetPathRoot(root), _volumeSpace.Root, StringComparison.OrdinalIgnoreCase);
    private string SelectedSize(DiskEntry entry) => _useAllocated ?
        entry.AllocatedSize is { } bytes ? $"分配大小 {DiskSize.Format(bytes)}{(entry.AllocationIncomplete ? "（部分未知）" : "")}" : "分配大小未知" : entry.SizeText;
    private static string FileSizeText(NodeInfo target) => "路径\t大小（字节）\t大小" + (target.Entry.AllocatedSize is null ? "" : "\t分配大小（字节）\t分配大小") + "\n" +
        target.Path + $"\t{target.Entry.Size}\t{target.Entry.SizeText}" + (target.Entry.AllocatedSize is { } allocated ? $"\t{allocated}\t{DiskSize.Format(allocated)}{(target.Entry.AllocationIncomplete ? "（部分未知）" : "")}" : "");
    private async Task CopyTextAsync(string value)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("剪贴板不可用");
        await clipboard.SetTextAsync(value);
    }
    private void BrowseTarget(NodeInfo target)
    {
        try { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; if (!target.Entry.Directory) start.ArgumentList.Add("/select,"); start.ArgumentList.Add(target.Path); Process.Start(start); }
        catch (Exception e) { _status.Text = e.Message; }
    }
    private async Task SetAllocatedAsync(bool enabled)
    {
        if (!enabled) { _useAllocated = false; UpdateMapHeader(); QueueMap(); return; }
        if (_snapshot is not { } snapshot) { _allocatedMenu.IsChecked = false; return; }
        _cancelRequested = false; _allocationRunning = true; UpdateButtons();
        _progress.IsIndeterminate = false;
        try
        {
            var state = await Task.Run(() => NativeScanner.StartAllocation(snapshot));
            while (!state.Done)
            {
                _status.Text = $"读取分配大小 {state.Processed:N0} / {state.Total:N0} · 未读取 {state.Failed:N0} · 可取消";
                _progress.Value = state.Total == 0 ? 0 : 100.0 * state.Processed / state.Total;
                await Task.Delay(150);
                state = await Task.Run(() => NativeScanner.AllocationState(snapshot));
            }
            if (state.Cancelled || _stopping) { _allocatedMenu.IsChecked = false; _status.Text = "分配大小读取已取消，继续显示文件大小"; return; }
            _useAllocated = true; _allocatedMenu.IsChecked = true; _allocationFailures = state.Failed;
            if (_selected is { } selected) await SelectAsync(selected.Entry.Id);
            UpdateMapHeader(); QueueMap();
            _status.Text = state.Failed == 0 ? "分配大小已就绪" : $"分配大小已就绪，{state.Failed:N0} 个文件无法读取；图中使用已读取的大小";
        }
        finally { _allocationRunning = false; _allocatedMenu.IsChecked = _useAllocated; _progress.Value = _snapshot is not null ? 100 : 0; UpdateButtons(); }
    }
    private bool CanDelete(NodeInfo? target)
    {
        if (target is null || target.Entry.Id == 0 || _scanRootPath is null || IsDeleted(target)) return false;
        var root = Path.GetFullPath(_scanRootPath);
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(target.Path);
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? ""), StringComparison.OrdinalIgnoreCase);
    }
    private async Task DeleteTargetAsync(NodeInfo target)
    {
        if (!CanDelete(target) || TopLevel.GetTopLevel(this) is not Window owner) return;
        var yes = new Button { Content = "移到回收站", Padding = new Thickness(12, 5) };
        var no = new Button { Content = "取消", Padding = new Thickness(12, 5) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { no, yes } };
        var dialog = _deleteDialog = new Window { Title = "确认删除", Width = 560, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(18), Spacing = 14, Children = { new TextBlock { Text = "将以下项目移到回收站？", FontSize = 16 },
                new TextBlock { Text = target.Path, TextWrapping = TextWrapping.Wrap }, Text(target.Entry.SizeText, 12), actions } } };
        dialog.RequestedThemeVariant = owner.ActualThemeVariant;
        void DialogThemeChanged(object? sender, EventArgs e) => dialog.RequestedThemeVariant = owner.ActualThemeVariant;
        owner.ActualThemeVariantChanged += DialogThemeChanged;
        dialog.Closed += (_, _) => owner.ActualThemeVariantChanged -= DialogThemeChanged;
        if (!_useHostTheme) dialog.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Edirstat.Client/")) { Source = new Uri("avares://Edirstat.Client/Themes/StandaloneColors.axaml") });
        ThemeBind(dialog, Window.BackgroundProperty, "BgSurfaceBrush"); ThemeBind(dialog, Window.ForegroundProperty, "FgDefaultBrush");
        yes.Classes.Add("buttonDanger"); no.Classes.Add("buttonSecondary");
        yes.Click += (_, _) => dialog.Close(true); no.Click += (_, _) => dialog.Close(false);
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(false); };
        bool confirmed;
        try { confirmed = await dialog.ShowDialog<bool>(owner); }
        finally { _deleteDialog = null; }
        if (!confirmed || _stopping || !CanDelete(target)) return;
        var handle = owner.TryGetPlatformHandle()?.Handle ?? 0;
        _status.Text = "正在删除：" + target.Path;
        if (!await WindowsDesktop.Recycle(target.Path, handle)) { _status.Text = "删除已取消"; return; }
        if (_stopping) return;
        _deleted[target.Entry.Id] = target;
        if (_root is { } root) MarkRows(root);
        if (_files.ItemsSource is IEnumerable<DiskEntry> files) foreach (var entry in files) TrackDeletion(entry);
        UpdateDeletedMap();
        if (_selected is { } selected) await SelectAsync(selected.Entry.Id);
        _status.Text = "已删除：" + target.Path + " · 保留当前布局和扫描时统计，点击扫描可刷新";
    }
    private void DeleteById(uint id)
    {
        Run(async () =>
        {
            if (_snapshot is not { } snapshot || id == uint.MaxValue) return;
            var target = await Task.Run(() => NativeScanner.Node(snapshot, id, uint.MaxValue));
            if (!CanDelete(target)) { _status.Text = IsDeleted(target) ? "该项目已删除。" : "扫描根目录和卷根不能删除。"; return; }
            await DeleteTargetAsync(target);
        });
    }
    private bool IsDeleted(NodeInfo target) => _deleted.ContainsKey(target.Entry.Id) || target.Ancestors.Any(_deleted.ContainsKey);
    private DiskEntry TrackDeletion(DiskEntry entry)
    {
        entry.IsDeleted = _deleted.ContainsKey(entry.Id) || entry.Path is { } path && _deleted.Values.Any(target =>
            string.Equals(path, target.Path, StringComparison.OrdinalIgnoreCase) || target.Entry.Directory &&
            path.StartsWith(Path.TrimEndingDirectorySeparator(target.Path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        return entry;
    }
    private void MarkRows(TreeRow row)
    {
        TrackDeletion(row.Entry);
        foreach (var child in row.Children) MarkRows(child);
    }
    private void UpdateDeletedMap() => _treemap.SetDeleted(_root is { Entry.IsDeleted: true } root ? _deleted.Keys.Append(root.Entry.Id) : _deleted.Keys);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SystemInformer.Client.Themes;

namespace SystemInformer.Client.StartupManagement;

/// <summary>Windows 启动项管理视图，嵌入为 BTM 的 StartupManagement 标签页。</summary>
[SupportedOSPlatform("windows")]
internal sealed class StartupManagementView : UserControl
{
    private readonly StartupManagementService _service = new();
    private readonly List<StartupItem> _allItems = [];
    private readonly DataGridCollectionView _view;
    private readonly DataGrid _grid;
    private readonly TextBox _searchBox;
    private readonly ComboBox _filterCombo;
    private readonly Button _toggleAddButton;
    private readonly Button _copyButton;
    private readonly Button _openDirButton;
    private readonly Button _removeButton;
    private readonly TextBlock _summaryLabel;
    private readonly TextBlock _statusLabel;
    private readonly TextBox _commandPreview;
    private readonly Border _addPanel;
    private readonly TextBox _newNameBox;
    private readonly TextBox _newPathBox;
    private readonly CheckBox _currentUserCheck;
    private readonly CheckBox _registryCheck;
    private readonly Button _confirmAddButton;
    private bool _loadedOnce;
    private bool _loading;

    public StartupManagementView()
    {
        _view = new DataGridCollectionView(_allItems) { Filter = item => item is StartupItem s && MatchesFilter(s) };
        _view.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(StartupItem.Name), ListSortDirection.Ascending));

        _searchBox = new TextBox
        {
            PlaceholderText = "搜索名称、路径、来源或说明",
            MinWidth = 230,
            MaxWidth = 360
        };
        _searchBox.TextChanged += (_, _) => _view.Refresh();

        _filterCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                "全部来源",
                "当前用户注册表",
                "系统注册表",
                "启动文件夹",
                "任务计划程序"
            },
            SelectedIndex = 0,
            MinWidth = 155
        };
        _filterCombo.SelectionChanged += (_, _) => _view.Refresh();

        _toggleAddButton = ActionButton("添加启动项", "buttonPrimary");
        _copyButton = ActionButton("复制命令", "buttonSecondary");
        _openDirButton = ActionButton("打开目录", "buttonSecondary");
        _removeButton = ActionButton("删除", "buttonDanger");

        _summaryLabel = MonitorTheme.Text("textSecondary", "共 0 项");
        _summaryLabel.VerticalAlignment = VerticalAlignment.Center;

        _statusLabel = MonitorTheme.Text("textSecondary", "就绪");
        _statusLabel.VerticalAlignment = VerticalAlignment.Center;

        _commandPreview = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "选中启动项后显示完整路径与启动参数",
            MaxHeight = 84,
            MinHeight = 32
        };
        _commandPreview.Classes.Add("servicePath");

        _grid = BuildGrid();
        _grid.ItemsSource = _view;
        _grid.SelectionChanged += (_, _) => UpdateSelectionState();
        _grid.DoubleTapped += (_, args) =>
        {
            if ((args.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is StartupItem)
                OpenSelectedDirectory();
        };

        var menuCopy = new MenuItem { Header = "复制启动命令" };
        var menuOpen = new MenuItem { Header = "打开文件所在位置" };
        var menuDelete = new MenuItem { Header = "删除启动项" };
        menuCopy.Click += (_, _) => _ = CopySelectedCommandAsync();
        menuOpen.Click += (_, _) => OpenSelectedDirectory();
        menuDelete.Click += (_, _) => RemoveSelectedItem();
        _grid.ContextMenu = new ContextMenu
        {
            Items = { menuCopy, menuOpen, new Separator(), menuDelete }
        };
        _grid.ContextMenu.Opening += (_, _) =>
        {
            var selected = SelectedItem;
            menuCopy.IsEnabled = selected != null;
            menuOpen.IsEnabled = selected != null;
            menuDelete.IsEnabled = selected is { CanRemove: true };
        };

        _newNameBox = new TextBox { PlaceholderText = "输入启动项名称（例如 MyApp）" };
        _newPathBox = new TextBox { PlaceholderText = "选择或输入程序路径（.exe / .bat / .cmd）" };
        _currentUserCheck = new CheckBox { Content = "仅添加到当前用户", IsChecked = true };
        _registryCheck = new CheckBox { Content = "写入注册表 Run（取消则创建启动文件夹快捷方式）", IsChecked = true };
        _confirmAddButton = ActionButton("确认添加", "buttonPrimary");

        _addPanel = BuildAddPanel();
        _addPanel.IsVisible = false;

        _toggleAddButton.Click += (_, _) => _addPanel.IsVisible = !_addPanel.IsVisible;
        _copyButton.Click += (_, _) => _ = CopySelectedCommandAsync();
        _openDirButton.Click += (_, _) => OpenSelectedDirectory();
        _removeButton.Click += (_, _) => RemoveSelectedItem();

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var control in new Control[] { _searchBox, _filterCombo, _toggleAddButton, _copyButton, _openDirButton, _removeButton })
        {
            control.Margin = new Thickness(0, 0, 6, 6);
            toolbar.Children.Add(control);
        }

        var topBar = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        topBar.Children.Add(toolbar);
        Grid.SetColumn(_summaryLabel, 1);
        topBar.Children.Add(_summaryLabel);

        var previewBar = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8 };
        var previewLabel = MonitorTheme.Text("textFieldLabel", "启动命令");
        previewLabel.VerticalAlignment = VerticalAlignment.Center;
        previewBar.Children.Add(previewLabel);
        Grid.SetColumn(_commandPreview, 1);
        previewBar.Children.Add(_commandPreview);

        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto"), RowSpacing = 8 };
        root.Children.Add(topBar);
        Grid.SetRow(_addPanel, 1);
        root.Children.Add(_addPanel);
        Grid.SetRow(previewBar, 2);
        root.Children.Add(previewBar);
        Grid.SetRow(_grid, 3);
        root.Children.Add(_grid);
        Grid.SetRow(_statusLabel, 4);
        root.Children.Add(_statusLabel);

        Content = root;
        UpdateSelectionState();
    }

    private StartupItem? SelectedItem => _grid.SelectedItem as StartupItem;

    public void EnsureLoaded()
    {
        if (_loadedOnce)
            return;

        _loadedOnce = true;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_loading)
            return;

        _loading = true;
        _statusLabel.Text = "正在扫描系统启动项...";

        try
        {
            var items = await _service.GetAllStartupItemsAsync();
            _allItems.Clear();
            _allItems.AddRange(items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase));
            _view.Refresh();
            UpdateSummary();
            UpdateSelectionState();
            _statusLabel.Text = $"已加载 {_allItems.Count} 个启动项";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"加载启动项失败: {ex.Message}";
        }
        finally
        {
            _loading = false;
        }
    }

    private Border BuildAddPanel()
    {
        var browseButton = ActionButton("浏览...", "buttonSecondary");
        browseButton.Click += async (_, _) => await BrowseProgramAsync();

        var cancelButton = ActionButton("取消", "buttonQuiet");
        cancelButton.Click += (_, _) => _addPanel.IsVisible = false;

        _confirmAddButton.Click += (_, _) => ConfirmAddItem();

        var form = new Grid
        {
            RowDefinitions = new("Auto,Auto,Auto"),
            ColumnDefinitions = new("68,*,Auto"),
            ColumnSpacing = 10,
            RowSpacing = 8
        };

        var nameLabel = MonitorTheme.Text("textFieldLabel", "名称");
        nameLabel.VerticalAlignment = VerticalAlignment.Center;
        form.Children.Add(nameLabel);
        Grid.SetColumn(_newNameBox, 1);
        Grid.SetColumnSpan(_newNameBox, 2);
        form.Children.Add(_newNameBox);

        var pathLabel = MonitorTheme.Text("textFieldLabel", "路径");
        pathLabel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(pathLabel, 1);
        form.Children.Add(pathLabel);
        Grid.SetRow(_newPathBox, 1);
        Grid.SetColumn(_newPathBox, 1);
        form.Children.Add(_newPathBox);
        Grid.SetRow(browseButton, 1);
        Grid.SetColumn(browseButton, 2);
        form.Children.Add(browseButton);

        var optionsRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        optionsRow.Children.Add(new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _currentUserCheck, _registryCheck }
        });
        _currentUserCheck.Margin = new Thickness(0, 0, 16, 0);

        var submitRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        submitRow.Children.Add(cancelButton);
        submitRow.Children.Add(_confirmAddButton);
        Grid.SetColumn(submitRow, 1);
        optionsRow.Children.Add(submitRow);

        Grid.SetRow(optionsRow, 2);
        Grid.SetColumn(optionsRow, 1);
        Grid.SetColumnSpan(optionsRow, 2);
        form.Children.Add(optionsRow);

        var card = new Border { Child = form };
        card.Classes.Add("monitorSurface");
        return card;
    }

    private async Task BrowseProgramAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择启动程序",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("可执行文件") { Patterns = ["*.exe", "*.bat", "*.cmd", "*.com"] },
                new FilePickerFileType("所有文件") { Patterns = ["*"] }
            ]
        });

        if (files.Count == 0)
            return;

        var localPath = files[0].Path.LocalPath;
        _newPathBox.Text = localPath;
        if (string.IsNullOrWhiteSpace(_newNameBox.Text))
            _newNameBox.Text = Path.GetFileNameWithoutExtension(localPath);
    }

    private void ConfirmAddItem()
    {
        var name = _newNameBox.Text?.Trim() ?? string.Empty;
        var path = _newPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path))
        {
            _statusLabel.Text = "请填写启动项名称和路径。";
            return;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            _statusLabel.Text = "指定的程序路径不存在，请核对后重试。";
            return;
        }

        try
        {
            var currentUser = _currentUserCheck.IsChecked != false;
            var toRegistry = _registryCheck.IsChecked != false;
            var item = new StartupItem
            {
                Name = name,
                Path = path,
                Description = toRegistry ? "用户添加的注册表启动项" : "用户添加的启动文件夹快捷方式"
            };

            var added = toRegistry
                ? _service.AddStartupItemToRegistry(item, currentUser)
                : _service.AddStartupItemToStartupFolder(item, currentUser);

            if (!added)
            {
                _statusLabel.Text = $"添加启动项失败: {name}";
                return;
            }

            _allItems.Add(item);
            _view.Refresh();
            _grid.SelectedItem = item;
            _newNameBox.Text = string.Empty;
            _newPathBox.Text = string.Empty;
            _addPanel.IsVisible = false;
            UpdateSummary();
            UpdateSelectionState();
            _statusLabel.Text = $"已添加启动项: {item.Name}";
        }
        catch (UnauthorizedAccessException)
        {
            _statusLabel.Text = "需要管理员权限才能添加系统级启动项。";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"添加启动项失败: {ex.Message}";
        }
    }

    private void RemoveSelectedItem()
    {
        var item = SelectedItem;
        if (item == null)
            return;

        if (!item.CanRemove)
        {
            _statusLabel.Text = "任务计划程序条目暂不支持在此直接删除。";
            return;
        }

        try
        {
            if (_service.RemoveStartupItem(item))
            {
                _allItems.Remove(item);
                _view.Refresh();
                UpdateSummary();
                UpdateSelectionState();
                _statusLabel.Text = $"已删除启动项: {item.Name}";
            }
            else
            {
                _statusLabel.Text = $"删除失败: 未找到 {item.Name}";
            }
        }
        catch (UnauthorizedAccessException)
        {
            _statusLabel.Text = "需要管理员权限才能删除系统级启动项。";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"删除失败: {ex.Message}";
        }
    }

    private async Task CopySelectedCommandAsync()
    {
        var item = SelectedItem;
        if (item == null)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;

        await clipboard.SetTextAsync(item.Path);
        _statusLabel.Text = $"已复制启动命令: {item.Name}";
    }

    private void OpenSelectedDirectory()
    {
        var item = SelectedItem;
        if (item == null)
            return;

        var dir = StartupManagementService.TryResolveTargetDirectory(item);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _statusLabel.Text = $"未能定位文件所在目录: {item.Path}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
            _statusLabel.Text = $"已打开目录: {dir}";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"打开目录失败: {ex.Message}";
        }
    }

    private bool MatchesFilter(StartupItem item)
    {
        var categoryMatches = _filterCombo.SelectedIndex switch
        {
            1 => item.Location == StartupLocation.RegistryCurrentUserRun,
            2 => item.Location == StartupLocation.RegistryLocalMachineRun,
            3 => item.Location is StartupLocation.StartupFolderCurrentUser or StartupLocation.StartupFolderAllUsers,
            4 => item.Location == StartupLocation.TaskScheduler,
            _ => true
        };

        if (!categoryMatches)
            return false;

        var keyword = _searchBox.Text?.Trim() ?? string.Empty;
        if (keyword.Length == 0)
            return true;

        return item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || item.Path.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || item.LocationDisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || item.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateSummary()
    {
        var visibleCount = 0;
        foreach (var _ in _view)
            visibleCount++;
        _summaryLabel.Text = visibleCount == _allItems.Count
            ? $"共 {_allItems.Count} 项"
            : $"显示 {visibleCount} / 共 {_allItems.Count} 项";
    }

    private void UpdateSelectionState()
    {
        var item = SelectedItem;
        _commandPreview.Text = item?.Path ?? string.Empty;
        _copyButton.IsEnabled = item != null;
        _openDirButton.IsEnabled = item != null;
        _removeButton.IsEnabled = item is { CanRemove: true };
        UpdateSummary();
    }

    private static DataGrid BuildGrid()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserSortColumns = true,
            CanUserReorderColumns = false,
            CanUserResizeColumns = true,
            SelectionMode = DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            RowHeight = 32,
            MinHeight = 180
        };

        grid.Columns.Add(Column("名称", nameof(StartupItem.Name), 210));
        grid.Columns.Add(Column("来源位置", nameof(StartupItem.LocationDisplayName), 165));
        grid.Columns.Add(Column("启动路径 / 命令", nameof(StartupItem.Path), 320, star: true));
        grid.Columns.Add(Column("说明", nameof(StartupItem.Description), 220));
        return grid;
    }

    private static DataGridTextColumn Column(string title, string path, double width, bool star = false) => new()
    {
        Header = title,
        Binding = new Binding(path) { Mode = BindingMode.OneWay, TargetNullValue = "—" },
        SortMemberPath = path,
        Width = star ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
        MinWidth = 80
    };

    private static Button ActionButton(string text, string semantic)
    {
        var button = new Button { Content = text };
        button.Classes.Add("contentButton");
        button.Classes.Add(semantic);
        button.Classes.Add("buttonSmall");
        return button;
    }
}

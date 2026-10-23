using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DockerManager.Wpf.Models;
using DockerManager.Wpf.Services;
using DockerManager.Wpf.Views;
using Microsoft.Win32;

namespace DockerManager.Wpf;

public partial class MainWindow : Window
{
    private readonly ProfileStore _store = new();
    private readonly ContainerClient _client = new();
    private readonly SettingsStore _settings = new();
    private readonly ObservableCollection<ConnectionProfile> _profiles = [];
    private IReadOnlyList<WorkloadItem> _items = [];
    private ViewKind _view = ViewKind.Docker;
    private bool _loadingProjects;
    private string _sortKey = nameof(WorkloadItem.Name);
    private bool _sortAscending = true;
    private int _loadCount;
    private string _lastLogDirectory = "";
    private CancellationTokenSource? _activeRequest;
    private CancellationTokenSource? _logCancellation;
    private bool _followingLogs;

    private ConnectionProfile? Profile => ConnectionList.SelectedItem as ConnectionProfile;
    private WorkloadItem? Selected => WorkloadsGrid.SelectedItem as WorkloadItem;

    // Log view state: a raw buffer plus a rendered document so filtering never loses lines.
    private readonly List<string> _logBuffer = [];
    private const int LogBufferLimit = 5000;
    private const int LogDocumentLimit = 3000;

    private static readonly Brush HighlightBackground = Brushes.Goldenrod;
    private static readonly Brush HighlightForeground = Brushes.Black;

    public MainWindow()
    {
        InitializeComponent();
        _client.ConfirmNewHostKey = ConfirmNewHostKeyAsync;
        ConnectionList.ItemsSource = _profiles;
        LoadSettings();
        try
        {
            foreach (var connection in _store.Load()) _profiles.Add(connection);
            if (_profiles.Count > 0) ConnectionList.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ShowError("无法读取连接配置。原文件没有被自动覆盖。", ex);
        }
        ConfigureColumns();
        UpdateModeUi();
    }

    private void LoadSettings()
    {
        var settings = _settings.Load();
        var minWidth = MinWidth > 0 ? MinWidth : 920;
        var minHeight = MinHeight > 0 ? MinHeight : 590;
        var screenWidth = SystemParameters.VirtualScreenWidth;
        var screenHeight = SystemParameters.VirtualScreenHeight;
        if (settings.WindowWidth >= minWidth && settings.WindowHeight >= minHeight &&
            settings.WindowWidth <= screenWidth && settings.WindowHeight <= screenHeight)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }
        // Keep the restored window fully on screen; otherwise fall back to the default placement.
        if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop) &&
            settings.WindowLeft >= SystemParameters.VirtualScreenLeft - 8 &&
            settings.WindowTop >= SystemParameters.VirtualScreenTop - 8 &&
            settings.WindowLeft + Width <= SystemParameters.VirtualScreenLeft + screenWidth + 8 &&
            settings.WindowTop + Height <= SystemParameters.VirtualScreenTop + screenHeight + 8)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }
        if (settings.WindowState is >= 0 and <= 2) WindowState = (WindowState)settings.WindowState;
        _lastLogDirectory = settings.LastLogDirectory;
    }

    private void SaveSettings()
    {
        var settings = new AppSettings
        {
            WindowState = WindowState == WindowState.Minimized ? 0 : (int)WindowState,
            LastLogDirectory = _lastLogDirectory
        };
        if (WindowState == WindowState.Normal)
        {
            settings.WindowLeft = Left;
            settings.WindowTop = Top;
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
        }
        else
        {
            settings.WindowLeft = RestoreBounds.Left;
            settings.WindowTop = RestoreBounds.Top;
            settings.WindowWidth = RestoreBounds.Width;
            settings.WindowHeight = RestoreBounds.Height;
        }
        try { _settings.Save(settings); }
        catch (Exception ex) { ShowError("保存界面设置失败。", ex); }
    }

    private async Task<bool> ConfirmNewHostKeyAsync(HostKeyPrompt prompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        const string verify = "在服务器本地控制台执行以下命令，与上面显示的指纹逐字符比对：\n" +
                             "ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub\n" +
                             "（若 SSH 使用其他主机密钥类型，请核对对应的 .pub 文件。）\n";
        var text = prompt.Issue == HostKeyIssue.KnownHostKeyChanged
            ? $"警告：SSH 服务器主机密钥与本机已记录的密钥不一致\n\n" +
              $"服务器：{prompt.Host}:{prompt.Port}\n" +
              $"服务器当前指纹：\n{prompt.Fingerprint}\n\n" +
              "常见原因：服务器重装或重建、系统镜像恢复、该 IP 现在指向另一台机器。\n" +
              "也可能是中间人攻击。\n\n" + verify + "\n" +
              "确认这是本机服务器的新密钥后，选择“是”，程序会替换保存的旧指纹；\n" +
              "若之前用命令行 ssh 连接过，可执行 ssh-keygen -R " + prompt.Host + " 清除 OpenSSH 旧记录。\n\n" +
              "无法确认请选择“否”。"
            : $"首次连接到 SSH 服务器：{prompt.Host}:{prompt.Port}\n\n" +
              $"服务器公钥 SHA256 指纹：\n{prompt.Fingerprint}\n\n" +
              verify + "\n" +
              "完全一致后才选择“是”；不一致或无法核对请选择“否”。";
        var dialog = Dispatcher.InvokeAsync(() => MessageBox.Show(
            this, text,
            prompt.Issue == HostKeyIssue.KnownHostKeyChanged ? "SSH 主机密钥已变化" : "确认服务器 SSH 指纹",
            MessageBoxButton.YesNo,
            prompt.Issue == HostKeyIssue.KnownHostKeyChanged ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No));
        return await dialog.Task.WaitAsync(ct) == MessageBoxResult.Yes;
    }

    private void SaveProfiles()
    {
        try { _store.Save(_profiles); }
        catch (Exception ex) { ShowError("保存连接配置失败。", ex); }
    }

    private void AddConnection_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ConnectionDialog { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile is null) return;
        _profiles.Add(dialog.Profile);
        SaveProfiles();
        ConnectionList.SelectedItem = dialog.Profile;
    }

    private void EditConnection_Click(object sender, RoutedEventArgs e)
    {
        if (Profile is not { } current) return;
        var dialog = new ConnectionDialog(current) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Profile is null) return;
        var index = _profiles.IndexOf(current);
        _profiles[index] = dialog.Profile;
        SaveProfiles();
        ConnectionList.SelectedIndex = index;
    }

    private void DeleteConnection_Click(object sender, RoutedEventArgs e)
    {
        if (Profile is not { } profile) return;
        if (MessageBox.Show(this, $"删除连接“{profile.Name}”？只删除本机配置，不影响远程服务。",
            "删除连接", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _profiles.Remove(profile);
        SaveProfiles();
    }

    private async void ConnectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopLogs();
        ClearLog();
        CancelRequest();
        var view = Profile?.Kind == ConnectionKind.Kubernetes ? ViewKind.Kubernetes : ViewKind.Docker;
        if (_view != view)
        {
            _view = view;
            _items = [];
            ApplyFilter();
        }
        UpdateModeUi();
        await RefreshAsync();
    }

    private async void DockerMode_Click(object sender, RoutedEventArgs e) => await SwitchViewAsync(ViewKind.Docker);
    private async void ComposeMode_Click(object sender, RoutedEventArgs e) => await SwitchViewAsync(ViewKind.Compose);
    private async void ImagesMode_Click(object sender, RoutedEventArgs e) => await SwitchViewAsync(ViewKind.Images);
    private async void NetworksMode_Click(object sender, RoutedEventArgs e) => await SwitchViewAsync(ViewKind.Networks);
    private async void VolumesMode_Click(object sender, RoutedEventArgs e) => await SwitchViewAsync(ViewKind.Volumes);
    private async void K8sMode_Click(object sender, RoutedEventArgs e) => await SwitchViewAsync(ViewKind.Kubernetes);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    /// <summary>Switching resource type rebuilds the columns, so stale rows must not linger.</summary>
    private async Task SwitchViewAsync(ViewKind view)
    {
        if (_view == view)
        {
            await RefreshAsync();
            return;
        }
        _view = view;
        _items = [];
        ApplyFilter();
        UpdateModeUi();
        await RefreshAsync();
    }

    /// <summary>Columns depend on the resource type; built in code so each view shows meaningful headers.</summary>
    private void ConfigureColumns()
    {
        WorkloadsGrid.Columns.Clear();
        (string Header, string Path, double Width)[] columns = _view switch
        {
            ViewKind.Images => [
                ("镜像", nameof(WorkloadItem.Name), 2.0),
                ("标签", nameof(WorkloadItem.Tags), 1.8),
                ("镜像 ID", nameof(WorkloadItem.Id), 1.6),
                ("大小", nameof(WorkloadItem.Size), 0.8),
                ("创建时间", nameof(WorkloadItem.Status), 1.1)],
            ViewKind.Networks => [
                ("名称", nameof(WorkloadItem.Name), 1.8),
                ("驱动", nameof(WorkloadItem.Driver), 0.9),
                ("作用域", nameof(WorkloadItem.Scope), 0.8),
                ("ID", nameof(WorkloadItem.Id), 1.8),
                ("创建时间", nameof(WorkloadItem.Status), 1.1)],
            ViewKind.Volumes => [
                ("名称", nameof(WorkloadItem.Name), 1.6),
                ("驱动", nameof(WorkloadItem.Driver), 0.8),
                ("挂载点", nameof(WorkloadItem.Mountpoint), 2.6),
                ("创建时间", nameof(WorkloadItem.Status), 1.1)],
            ViewKind.Kubernetes => [
                ("名称 / Pod", nameof(WorkloadItem.Name), 2.0),
                ("容器", nameof(WorkloadItem.Container), 1.1),
                ("命名空间", nameof(WorkloadItem.Namespace), 1.0),
                ("镜像", nameof(WorkloadItem.Image), 1.8),
                ("状态", nameof(WorkloadItem.State), 0.8),
                ("详情状态", nameof(WorkloadItem.Status), 1.2)],
            _ => [
                ("名称", nameof(WorkloadItem.Name), 2.0),
                ("容器", nameof(WorkloadItem.Container), 1.1),
                ("命名空间", nameof(WorkloadItem.Namespace), 1.0),
                ("镜像", nameof(WorkloadItem.Image), 1.8),
                ("状态", nameof(WorkloadItem.State), 0.8),
                ("详情状态", nameof(WorkloadItem.Status), 1.2),
                ("Compose 项目", nameof(WorkloadItem.Project), 1.1),
                ("服务", nameof(WorkloadItem.Service), 0.9)]
        };
        foreach (var (header, path, width) in columns)
        {
            WorkloadsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                SortMemberPath = path,
                Width = new DataGridLength(width, DataGridLengthUnitType.Star)
            });
        }
        // Keep the sort indicator consistent with the freshly created columns.
        var sorted = WorkloadsGrid.Columns.FirstOrDefault(c => c.SortMemberPath == _sortKey);
        if (sorted is not null)
            sorted.SortDirection = _sortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
    }

    private void UpdateModeUi()
    {
        var profile = Profile;
        CurrentTitle.Text = profile?.Name ?? "请选择连接";
        Title = profile is null ? "DockerManager - 远程容器管理器" : $"DockerManager - {profile.Name}";
        bool docker = profile != null && profile.Kind != ConnectionKind.Kubernetes;
        DockerModeButton.IsEnabled = docker;
        ComposeModeButton.IsEnabled = docker;
        ImagesModeButton.IsEnabled = docker;
        NetworksModeButton.IsEnabled = docker;
        VolumesModeButton.IsEnabled = docker;
        K8sModeButton.IsEnabled = profile?.Kind == ConnectionKind.Kubernetes;
        DockerModeButton.FontWeight = _view == ViewKind.Docker ? FontWeights.Bold : FontWeights.Normal;
        ComposeModeButton.FontWeight = _view == ViewKind.Compose ? FontWeights.Bold : FontWeights.Normal;
        ImagesModeButton.FontWeight = _view == ViewKind.Images ? FontWeights.Bold : FontWeights.Normal;
        NetworksModeButton.FontWeight = _view == ViewKind.Networks ? FontWeights.Bold : FontWeights.Normal;
        VolumesModeButton.FontWeight = _view == ViewKind.Volumes ? FontWeights.Bold : FontWeights.Normal;
        K8sModeButton.FontWeight = _view == ViewKind.Kubernetes ? FontWeights.Bold : FontWeights.Normal;
        ProjectBox.Visibility = _view == ViewKind.Compose && docker ? Visibility.Visible : Visibility.Collapsed;
        ConfigureColumns();
        UpdateSelectionUi();
    }

    /// <summary>Prevents duplicate requests while a load is already running.</summary>
    private void BeginLoad()
    {
        _loadCount++;
        RefreshButton.IsEnabled = false;
        TestButton.IsEnabled = false;
    }

    private void EndLoad()
    {
        if (--_loadCount <= 0)
        {
            _loadCount = 0;
            RefreshButton.IsEnabled = true;
            TestButton.IsEnabled = true;
        }
    }

    private CancellationToken StartRequest()
    {
        CancelRequest();
        _activeRequest = new CancellationTokenSource();
        return _activeRequest.Token;
    }

    private void CancelRequest()
    {
        _activeRequest?.Cancel();
        _activeRequest = null;
    }

    private void WorkloadsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionUi();

    private void WorkloadsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // Sorting is applied by ApplyFilter, so the order survives refreshes and filtering.
        e.Handled = true;
        var key = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(key)) return;
        if (_sortKey == key) _sortAscending = !_sortAscending;
        else { _sortKey = key; _sortAscending = true; }
        e.Column.SortDirection = _sortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        ApplyFilter();
    }

    private void WorkloadsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null) Inspect_Click(InspectButton, new RoutedEventArgs());
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { Refresh_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SearchBox.Clear();
        e.Handled = true;
    }

    private void UpdateSelectionUi()
    {
        var selected = Selected is not null && Profile is not null;
        var dockerProfile = Profile?.Kind != ConnectionKind.Kubernetes;
        var canManageDocker = selected && dockerProfile && Selected!.Kind == ResourceKind.Container;
        StartButton.IsEnabled = StopButton.IsEnabled = RestartButton.IsEnabled = canManageDocker;
        InspectButton.IsEnabled = selected;
        LogButton.IsEnabled = selected || _followingLogs;
        bool manageable = selected && dockerProfile && Selected!.Kind is ResourceKind.Image or ResourceKind.Network or ResourceKind.Volume;
        DeleteButton.IsEnabled = manageable;
        CreateButton.IsEnabled = dockerProfile && _view is ViewKind.Networks or ViewKind.Volumes;
    }

    private async Task RefreshAsync()
    {
        StopLogs();
        var profile = Profile;
        var selectedView = _view;
        var ct = StartRequest();
        if (profile is null)
        {
            _items = [];
            ApplyFilter();
            StatusBox.Text = "请添加一个连接";
            return;
        }
        // Keep the previous list visible while loading instead of flashing an empty grid.
        BeginLoad();
        StatusBox.Text = "正在连接及加载...";
        try
        {
            if (selectedView == ViewKind.Compose)
            {
                var selectedProject = (ProjectBox.SelectedItem as ComposeProject)?.Name;
                var projects = await _client.ComposeProjectsAsync(profile, ct);
                ct.ThrowIfCancellationRequested();
                _loadingProjects = true;
                try
                {
                    ProjectBox.ItemsSource = projects;
                    ProjectBox.SelectedItem = projects.FirstOrDefault(p => p.Name == selectedProject) ?? projects.FirstOrDefault();
                }
                finally { _loadingProjects = false; }
                if (ProjectBox.SelectedItem is ComposeProject project)
                    _items = await _client.ComposeContainersAsync(profile, project.Name, ct);
            }
            else if (selectedView == ViewKind.Kubernetes)
                _items = await _client.PodsAsync(profile, ct);
            else if (selectedView == ViewKind.Images)
                _items = await _client.ImagesAsync(profile, ct);
            else if (selectedView == ViewKind.Networks)
                _items = await _client.NetworksAsync(profile, ct);
            else if (selectedView == ViewKind.Volumes)
                _items = await _client.VolumesAsync(profile, ct);
            else
                _items = await _client.DockerContainersAsync(profile, ct);
            ct.ThrowIfCancellationRequested();
            if (Profile?.Id != profile.Id || _view != selectedView) return;
            StatusBox.Text = selectedView == ViewKind.Compose
                ? ProjectBox.Items.Count == 0
                    ? "未发现 Compose 项目"
                    : $"Compose 项目：{(ProjectBox.SelectedItem as ComposeProject)?.Name}（{_items.Count} 个容器）"
                : $"已连接：{profile.Name}";
            ApplyFilter();
        }
        catch (OperationCanceledException) { /* A newer selection took over. */ }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            StatusBox.Text = _items.Count > 0 ? "连接或加载失败（仍显示上次的数据）" : "连接或加载失败";
            ShowError("连接或加载失败。", ex);
        }
        finally { EndLoad(); }
    }

    private async void ProjectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProjects || Profile is null || _view != ViewKind.Compose) return;
        StopLogs();
        if (ProjectBox.SelectedItem is not ComposeProject project) { _items = []; ApplyFilter(); return; }
        var profile = Profile;
        var ct = StartRequest();
        BeginLoad();
        StatusBox.Text = "正在加载 Compose 项目...";
        try
        {
            var result = await _client.ComposeContainersAsync(profile, project.Name, ct);
            ct.ThrowIfCancellationRequested();
            if (Profile?.Id != profile.Id || _view != ViewKind.Compose) return;
            _items = result;
            ApplyFilter();
            StatusBox.Text = $"Compose 项目：{project.Name}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) ShowError("加载 Compose 项目失败。", ex); }
        finally { EndLoad(); }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var search = SearchBox?.Text.Trim() ?? "";
        var selectedId = Selected is { } prior ? (prior.Id, prior.Namespace, prior.Container) : ((string, string, string)?)null;
        var visible = string.IsNullOrWhiteSpace(search) ? _items : _items.Where(x =>
            new[] { x.Name, x.Image, x.Namespace, x.Container, x.Project, x.Service, x.State }
                .Any(v => v.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
        Func<WorkloadItem, string> key = _sortKey switch
        {
            nameof(WorkloadItem.Container) => x => x.Container,
            nameof(WorkloadItem.Namespace) => x => x.Namespace,
            nameof(WorkloadItem.Image) => x => x.Image,
            nameof(WorkloadItem.State) => x => x.State,
            nameof(WorkloadItem.Status) => x => x.Status,
            nameof(WorkloadItem.Project) => x => x.Project,
            nameof(WorkloadItem.Service) => x => x.Service,
            nameof(WorkloadItem.Id) => x => x.Id,
            nameof(WorkloadItem.Tags) => x => x.Tags,
            nameof(WorkloadItem.Size) => x => x.Size,
            nameof(WorkloadItem.Driver) => x => x.Driver,
            nameof(WorkloadItem.Scope) => x => x.Scope,
            nameof(WorkloadItem.Mountpoint) => x => x.Mountpoint,
            _ => x => x.Name
        };
        visible = _sortAscending
            ? visible.OrderBy(key, StringComparer.OrdinalIgnoreCase).ToList()
            : visible.OrderByDescending(key, StringComparer.OrdinalIgnoreCase).ToList();
        WorkloadsGrid.ItemsSource = visible;
        CountBox.Text = $"{visible.Count} 项";
        if (selectedId != null) WorkloadsGrid.SelectedItem = visible.FirstOrDefault(x => (x.Id, x.Namespace, x.Container) == selectedId.Value);
        UpdateSelectionUi();
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (Profile is not { } profile) return;
        var ct = StartRequest();
        BeginLoad();
        StatusBox.Text = "正在测试连接...";
        try
        {
            var result = await _client.TestAsync(profile, ct);
            if (ct.IsCancellationRequested) return;
            StatusBox.Text = "测试连接成功";
            MessageBox.Show(this, "连接成功！\n" + result, "连接测试", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) ShowError("连接测试失败。", ex); }
        finally { EndLoad(); }
    }

    private async void ContainerAction_Click(object sender, RoutedEventArgs e)
    {
        if (Profile is not { } profile || Selected is not { } entry || sender is not Button button ||
            button.Tag is not string action || profile.Kind == ConnectionKind.Kubernetes) return;
        if (MessageBox.Show(this, $"确认对容器 {entry.Name} 执行 {button.Content}？", "容器操作",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var ct = StartRequest();
        BeginLoad();
        try
        {
            await _client.ContainerActionAsync(profile, entry, action, ct);
            if (ct.IsCancellationRequested) return;
            StatusBox.Text = "操作成功：" + entry.Name;
            await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) ShowError("容器操作失败。", ex); }
        finally { EndLoad(); }
    }

    private async void DeleteResource_Click(object sender, RoutedEventArgs e)
    {
        if (Profile is not { } profile || Selected is not { } entry) return;
        var label = entry.Kind switch
        {
            ResourceKind.Image => "镜像",
            ResourceKind.Network => "网络",
            ResourceKind.Volume => "存储卷",
            _ => "资源"
        };
        if (MessageBox.Show(this, $"确认删除{label}“{entry.Name}”？删除后不可恢复。", $"删除{label}",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var ct = StartRequest();
        BeginLoad();
        try
        {
            await _client.DeleteAsync(profile, entry, ct);
            if (ct.IsCancellationRequested) return;
            StatusBox.Text = $"已删除{label}：{entry.Name}";
            await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) ShowError($"删除{label}失败。", ex); }
        finally { EndLoad(); }
    }

    private async void CreateResource_Click(object sender, RoutedEventArgs e)
    {
        if (Profile is not { } profile) return;
        var isVolume = _view == ViewKind.Volumes;
        var dialog = new InputDialog(
            isVolume ? "新建存储卷" : "新建网络",
            isVolume ? "为 Docker 创建一个命名存储卷。" : "为 Docker 创建一个网络。",
            "",
            isVolume ? ["local"] : ["bridge", "overlay", "macvlan", "ipvlan"],
            isVolume ? null : "内部网络（不提供外部通信）")
        { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var name = dialog.Value;
        var driver = dialog.Driver;
        var internalOnly = dialog.Checked;
        var ct = StartRequest();
        BeginLoad();
        StatusBox.Text = isVolume ? "正在创建存储卷..." : "正在创建网络...";
        try
        {
            if (isVolume) await _client.CreateVolumeAsync(profile, name, driver, ct);
            else await _client.CreateNetworkAsync(profile, name, driver, internalOnly, ct);
            if (ct.IsCancellationRequested) return;
            StatusBox.Text = $"已创建{(isVolume ? "存储卷" : "网络")}：{name}";
            await RefreshAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) ShowError("创建失败。", ex); }
        finally { EndLoad(); }
    }

    private async void Inspect_Click(object sender, RoutedEventArgs e)
    {
        if (Profile is not { } profile || Selected is not { } entry) return;
        StopLogs();
        var ct = StartRequest();
        BeginLoad();
        try
        {
            var detail = await _client.InspectAsync(profile, entry, ct);
            if (ct.IsCancellationRequested) return;
            SetLogContent(detail);
            LogBox.ScrollToHome();
            StatusBox.Text = "已读取详情：" + entry.Name;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) ShowError("获取详情失败。", ex); }
        finally { EndLoad(); }
    }

    private void AppendLog(string text)
    {
        _logBuffer.Add(text);
        if (_logBuffer.Count > LogBufferLimit)
            _logBuffer.RemoveRange(0, _logBuffer.Count - LogBufferLimit);
        if (!MatchesLogFilter(text)) return;
        AppendLineToDocument(text);
        TrimDocument();
    }

    private void SetLogContent(string text)
    {
        _logBuffer.Clear();
        foreach (var line in text.Split('\n'))
            _logBuffer.Add(line.TrimEnd('\r'));
        RebuildLog();
    }

    private bool MatchesLogFilter(string line)
    {
        var keyword = LogFilterBox?.Text.Trim() ?? "";
        return keyword.Length == 0 || line.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void LogFilter_TextChanged(object sender, TextChangedEventArgs e) => RebuildLog();

    private void LogFilter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        LogFilterBox.Clear();
        e.Handled = true;
    }

    /// <summary>Re-renders the buffered lines with the current filter applied.</summary>
    private void RebuildLog()
    {
        LogBox.Document.Blocks.Clear();
        var keyword = LogFilterBox?.Text.Trim() ?? "";
        var matching = _logBuffer.Where(MatchesLogFilter).TakeLast(LogDocumentLimit);
        foreach (var line in matching) AppendLineToDocument(line);
        LogBox.ScrollToEnd();
    }

    private void AppendLineToDocument(string line)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var keyword = LogFilterBox?.Text.Trim() ?? "";
        if (keyword.Length == 0)
        {
            paragraph.Inlines.Add(new Run(line));
        }
        else
        {
            var index = 0;
            while (index <= line.Length)
            {
                var hit = line.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    paragraph.Inlines.Add(new Run(line[index..]));
                    break;
                }
                if (hit > index) paragraph.Inlines.Add(new Run(line[index..hit]));
                paragraph.Inlines.Add(new Run(line.Substring(hit, keyword.Length))
                {
                    Background = HighlightBackground,
                    Foreground = HighlightForeground,
                    FontWeight = FontWeights.Bold
                });
                index = hit + keyword.Length;
                if (index >= line.Length) break;
            }
        }
        LogBox.Document.Blocks.Add(paragraph);
    }

    private void TrimDocument()
    {
        while (LogBox.Document.Blocks.Count > LogDocumentLimit && LogBox.Document.Blocks.FirstBlock is { } first)
            LogBox.Document.Blocks.Remove(first);
    }

    private async void Logs_Click(object sender, RoutedEventArgs e)
    {
        if (_followingLogs) { StopLogs(); return; }
        if (Profile is not { } profile || Selected is not { } entry) return;
        if (!int.TryParse(TailBox.Text, out var tail) || tail < 1 || tail > 100000)
        {
            MessageBox.Show(this, "日志行数请输入 1～100000 之间的整数。", "日志参数");
            return;
        }
        StopLogs();
        var source = new CancellationTokenSource();
        _logCancellation = source;
        _followingLogs = true;
        LogButton.Content = "停止日志";
        ClearLog();
        StatusBox.Text = $"正在读取日志：{entry.Name}" + (entry.Container.Length > 0 ? $"（容器 {entry.Container}）" : "");
        try
        {
            await _client.StreamLogsAsync(profile, entry, tail, FollowCheck.IsChecked == true,
                line => Dispatcher.BeginInvoke(() => AppendLog(line)), source.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!source.IsCancellationRequested) AppendLog("[错误] " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_logCancellation, source))
            {
                _logCancellation = null;
                _followingLogs = false;
                LogButton.Content = "查看日志";
                UpdateSelectionUi();
            }
            source.Dispose();
        }
    }

    private void StopLogs()
    {
        _logCancellation?.Cancel();
        _followingLogs = false;
        LogButton.Content = "查看日志";
        UpdateSelectionUi();
    }

    private void SaveLogs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt",
            FileName = "container.log"
        };
        if (!string.IsNullOrEmpty(_lastLogDirectory) && Directory.Exists(_lastLogDirectory))
            dialog.InitialDirectory = _lastLogDirectory;
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            // Save exactly what the log view currently shows, including the active filter.
            var range = new TextRange(LogBox.Document.ContentStart, LogBox.Document.ContentEnd);
            File.WriteAllText(dialog.FileName, range.Text);
            _lastLogDirectory = Path.GetDirectoryName(dialog.FileName) ?? _lastLogDirectory;
            StatusBox.Text = "已保存日志";
        }
        catch (Exception ex) { ShowError("日志保存失败。", ex); }
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e) => ClearLog();

    private void ClearLog()
    {
        _logBuffer.Clear();
        LogBox.Document.Blocks.Clear();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        StopLogs();
        CancelRequest();
        SaveSettings();
        _client.Dispose();
    }

    private void ShowError(string title, Exception ex)
    {
        // SSH trust rejections carry actionable guidance; show every layer so a wrapped
        // library exception never hides the explanation.
        var messages = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            var message = current.Message.Trim();
            if (message.Length > 0 && !messages.Contains(message)) messages.Add(message);
        }
        // Fallback: even if a library layer drops the guidance, common SSH failures stay actionable.
        var combined = string.Join("\n", messages);
        if (combined.Contains("is not trusted", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("UntrustedPeer", StringComparison.OrdinalIgnoreCase))
            messages.Add("SSH 主机密钥未通过校验：请核对服务器指纹后再确认；服务器确实重装过时可执行 " +
                         "ssh-keygen -R <主机> 清除 OpenSSH 旧记录后重连。");
        else if (combined.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                 combined.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            messages.Add("SSH 认证被拒绝：请确认连接使用的账号有权访问 Docker Socket，" +
                         "公钥已加入服务器 ~/.ssh/authorized_keys，或在 ~/.ssh/config 中为该主机配置 IdentityFile。");
        MessageBox.Show(this, title + "\n\n" + string.Join("\n\n", messages), "DockerManager",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

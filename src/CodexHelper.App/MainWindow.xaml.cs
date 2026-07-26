using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;
using CodexHelper.Core.Services;

namespace CodexHelper.App;

public partial class MainWindow : Window
{
    private readonly AppPaths appPaths = new();
    private readonly AppLogger logger;
    private readonly SettingsService settingsService;
    private readonly CodexProcessService processService = new();
    private readonly CodexDataDiscovery codexDiscovery = new();
    private readonly ProjectDiscovery projectDiscovery = new();
    private AppSettings settings;
    private IReadOnlyList<DataInventoryItem> inventory = Array.Empty<DataInventoryItem>();
    private IReadOnlyList<ProjectInfo> projects = Array.Empty<ProjectInfo>();
    private CancellationTokenSource? operationCancellation;
    private WelcomeWindow? guideWindow;
    private bool operationRunning;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "—");
        logger = new AppLogger(appPaths);
        settingsService = new SettingsService(appPaths);
        settings = settingsService.Load();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        ApplySettingsToUi();
        await RefreshAllAsync();
        SelectPage(string.IsNullOrWhiteSpace(settings.LastSelectedPage) ? "Dashboard" : settings.LastSelectedPage);
        if (!settings.HasCompletedOnboarding) ShowGuide(markOnboardingComplete: true);
    }

    private void ApplySettingsToUi()
    {
        CodexRootBox.Text = settings.CodexRoot;
        RepositoryPathText.Text = string.IsNullOrWhiteSpace(settings.BackupRepositoryPath) ? "尚未设置" : settings.BackupRepositoryPath;
        WorkspaceRootBox.Text = settings.WorkspaceRoots.FirstOrDefault() ?? string.Empty;
        IncludeSessionsCheck.IsChecked = settings.IncludeSessions;
        IncludeAttachmentsCheck.IsChecked = settings.IncludeAttachments;
        IncludeGeneratedCheck.IsChecked = settings.IncludeGeneratedImages;
    }

    private async Task RefreshAllAsync()
    {
        await RunOperationAsync("扫描", async cancellationToken =>
        {
            inventory = await codexDiscovery.DiscoverAsync(settings, cancellationToken);
            projects = await projectDiscovery.DiscoverAsync(settings.WorkspaceRoots, settings.ProtectedProjectPaths, cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                InventoryGrid.ItemsSource = inventory;
                ProjectsGrid.ItemsSource = projects;
                RefreshConnections();
                RefreshSnapshots();
                RefreshDashboard();
            });
        }, showProgress: false);
    }

    private void RefreshConnections()
    {
        try
        {
            var service = new OfficialAccountService(settings.CodexRoot, appPaths, processService);
            ConnectionsGrid.ItemsSource = service.LoadIndex().Profiles.OrderByDescending(item => item.IsActive).ThenBy(item => item.Label).ToList();
            RefreshAccountHealthDetail();
        }
        catch
        {
            ConnectionsGrid.ItemsSource = Array.Empty<ConnectionProfile>();
        }
    }

    private void ConnectionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshAccountHealthDetail();

    private void RefreshAccountHealthDetail()
    {
        if (AccountHealthDetailText is null) return;
        if (ConnectionsGrid.SelectedItem is not ConnectionProfile { Kind: ConnectionKind.OfficialAccount } profile)
        {
            AccountHealthDetailText.Text = "选择一个官方账号可查看套餐、额度窗口、重置时间和最近检测历史。";
            return;
        }
        var history = new OfficialAccountService(settings.CodexRoot, appPaths, processService).GetHealthHistory(profile.Id).Take(3).ToList();
        string Percent(decimal? used) => used is null ? "官方未提供" : $"剩余 {100 - used.Value:0.#}%（已用 {used.Value:0.#}%）";
        string Reset(DateTime? time) => time is null ? "官方未提供" : time.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var recent = history.Count == 0 ? "暂无检测历史" : string.Join("；", history.Select(item => item.CheckedUtc.ToLocalTime().ToString("MM-dd HH:mm") + " " + (item.IsAvailable ? "可用" : "异常")));
        AccountHealthDetailText.Text = $"账号：{profile.IdentityHint}\n状态：{profile.StatusMessage}\n套餐：{(string.IsNullOrWhiteSpace(profile.PlanName) ? "官方未提供" : profile.PlanName)}\n短周期：{Percent(profile.PrimaryUsedPercent)}，重置：{Reset(profile.PrimaryResetsAtUtc)}\n长周期：{Percent(profile.SecondaryUsedPercent)}，重置：{Reset(profile.SecondaryResetsAtUtc)}\n最近检测：{(profile.LastVerifiedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未检测")}\n历史：{recent}";
    }

    private void RefreshSnapshots()
    {
        if (string.IsNullOrWhiteSpace(settings.BackupRepositoryPath) || !File.Exists(Path.Combine(settings.BackupRepositoryPath, "repository.json")))
        {
            SnapshotsGrid.ItemsSource = Array.Empty<SnapshotSummary>();
            return;
        }
        try { SnapshotsGrid.ItemsSource = new BackupRepository(settings.BackupRepositoryPath).ListSnapshots(); }
        catch { SnapshotsGrid.ItemsSource = Array.Empty<SnapshotSummary>(); }
    }

    private void RefreshDashboard()
    {
        var connections = ConnectionsGrid.ItemsSource as IEnumerable<ConnectionProfile>;
        var active = connections?.FirstOrDefault(item => item.IsActive);
        CurrentConnectionText.Text = active?.Label ?? "官方登录 / 未关联档案";
        ConnectionDetailText.Text = active?.DisplayTarget ?? settings.CodexRoot;
        ProtectedProjectText.Text = settings.ProtectedProjectPaths.Count + " 个";
        ProjectDetailText.Text = projects.Count + " 个已发现项目";
        var latest = (SnapshotsGrid.ItemsSource as IEnumerable<SnapshotSummary>)?.OrderByDescending(item => item.CreatedUtc).FirstOrDefault();
        LastSnapshotText.Text = latest is null ? "尚未备份" : latest.CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm");
        SnapshotDetailText.Text = latest is null ? "请选择备份仓库并创建基线" : $"{latest.FileCount} 个文件 · {latest.Outcome}";
    }

    private void Navigate_Checked(object sender, RoutedEventArgs e)
    {
        // The initially checked navigation button is constructed before the
        // content pages exist. Defer navigation until the window is loaded.
        if (!IsLoaded) return;
        if (sender is RadioButton { Tag: string page }) SelectPage(page);
    }

    private void SelectPage(string page)
    {
        if (!IsLoaded && page != "Dashboard") return;
        var pages = new Dictionary<string, FrameworkElement>
        {
            ["Dashboard"] = DashboardPage,
            ["Connections"] = ConnectionsPage,
            ["Projects"] = ProjectsPage,
            ["Snapshots"] = SnapshotsPage,
            ["Migration"] = MigrationPage,
            ["Health"] = HealthPage,
            ["Settings"] = SettingsPage
        };
        if (!pages.ContainsKey(page)) page = "Dashboard";
        var navigation = new Dictionary<string, RadioButton>
        {
            ["Dashboard"] = DashboardNav,
            ["Connections"] = ConnectionsNav,
            ["Projects"] = ProjectsNav,
            ["Snapshots"] = SnapshotsNav,
            ["Migration"] = MigrationNav,
            ["Health"] = HealthNav,
            ["Settings"] = SettingsNav
        };
        if (navigation.TryGetValue(page, out var selectedNav) && selectedNav.IsChecked != true) selectedNav.IsChecked = true;
        foreach (var pair in pages) pair.Value.Visibility = pair.Key == page ? Visibility.Visible : Visibility.Collapsed;
        settings.LastSelectedPage = page;
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(settings.BackupRepositoryPath))
        {
            MessageBox.Show("请先在“快照中心”选择备份仓库。", "尚未设置备份仓库", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var sources = BuildBackupSources();
        if (sources.Count == 0)
        {
            MessageBox.Show("没有找到可保护的数据。请检查 Codex 根目录或添加重要项目。", "没有数据源", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunOperationAsync("创建快照", async cancellationToken =>
        {
            var repository = new BackupRepository(settings.BackupRepositoryPath);
            var result = await repository.CreateSnapshotAsync("一键保护", sources, CreateProgress(), cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                RefreshSnapshots();
                RefreshDashboard();
                MessageBox.Show($"快照完成：{result.Summary.FileCount} 个文件，结果 {result.Summary.Outcome}。\n新增存储：{FormatBytes(result.Summary.NewStoredBytes)}", "保护完成", MessageBoxButton.OK, result.Summary.Outcome == OperationOutcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            });
        });
    }

    private List<BackupSource> BuildBackupSources()
    {
        var result = new List<BackupSource>();
        var root = settings.CodexRoot;
        AddFileSource(result, "codex-config", "Codex 全局配置", Path.Combine(root, "config.toml"));
        AddFileSource(result, "codex-agents", "Codex 全局 AGENTS", Path.Combine(root, "AGENTS.md"));
        AddFileSource(result, "codex-hooks", "Codex Hooks", Path.Combine(root, "hooks.json"));
        if (Directory.Exists(root))
        {
            foreach (var profile in Directory.EnumerateFiles(root, "*.config.toml", SearchOption.TopDirectoryOnly))
                AddFileSource(result, "codex-profile-" + ShortHash(profile), "Codex Profile " + Path.GetFileName(profile), profile);
        }
        AddDirectorySource(result, "skills", "个人 Skills", Path.Combine(root, "skills"), additionalExcludes: [".system"]);
        if (settings.IncludeSessions)
        {
            AddDirectorySource(result, "codex-sessions", "Codex 任务", Path.Combine(root, "sessions"));
            AddDirectorySource(result, "codex-archived", "Codex 归档任务", Path.Combine(root, "archived_sessions"));
            AddFileSource(result, "codex-session-index", "Codex 会话索引", Path.Combine(root, "session_index.jsonl"));
        }
        if (settings.IncludeAttachments)
        {
            AddDirectorySource(result, "codex-attachments", "Codex 附件", Path.Combine(root, "attachments"));
            AddDirectorySource(result, "codex-uploads", "Codex 上传", Path.Combine(root, "web-uploads"));
        }
        if (settings.IncludeGeneratedImages) AddDirectorySource(result, "codex-generated", "Codex 生成图片", Path.Combine(root, "generated_images"));

        if (processService.GetRunningProcesses().Count == 0)
        {
            AddFileSource(result, "codex-state", "Codex 状态数据库", Path.Combine(root, "state_5.sqlite"));
            AddFileSource(result, "codex-active-state", "Codex 活动状态数据库", Path.Combine(root, "sqlite", "state_5.sqlite"));
            AddFileSource(result, "codex-memories", "Codex 记忆数据库", Path.Combine(root, "memories_1.sqlite"));
            AddFileSource(result, "codex-goals", "Codex 目标数据库", Path.Combine(root, "goals_1.sqlite"));
        }

        foreach (var projectPath in settings.ProtectedProjectPaths.Where(Directory.Exists))
            result.Add(new BackupSource("project-" + ShortHash(projectPath), Path.GetFileName(projectPath), projectPath, UseDevelopmentExcludes: true));
        return result;
    }

    private static void AddFileSource(List<BackupSource> result, string id, string name, string path)
    {
        if (File.Exists(path)) result.Add(new BackupSource(id, name, path));
    }

    private static void AddDirectorySource(List<BackupSource> result, string id, string name, string path, IReadOnlyList<string>? additionalExcludes = null)
    {
        if (Directory.Exists(path)) result.Add(new BackupSource(id, name, path, AdditionalExcludedDirectoryNames: additionalExcludes));
    }

    private async void RestoreSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsGrid.SelectedItem is not SnapshotSummary snapshot) { MessageBox.Show("请选择一个快照。"); return; }
        var dialog = new OpenFolderDialog { Title = "选择恢复目标；Codex Helper 会在其中按数据源创建子目录" };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperationAsync("恢复快照", async cancellationToken =>
        {
            var repository = new BackupRepository(settings.BackupRepositoryPath);
            var result = await repository.RestoreAsync(new RestoreRequest(snapshot.Id, dialog.FolderName), CreateProgress(), cancellationToken);
            await Dispatcher.InvokeAsync(() => MessageBox.Show($"已恢复 {result.RestoredFiles} 个文件到：\n{dialog.FolderName}\n结果：{result.Outcome}", "恢复完成", MessageBoxButton.OK, result.Outcome == OperationOutcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning));
        });
    }

    private async void SaveOfficial_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureCodexStoppedAsync()) return;
        // Capture UI state before leaving the Dispatcher thread.
        var label = OfficialLabelBox.Text;
        await RunOperationAsync("保存账号", cancellationToken => Task.Run(() =>
        {
            new OfficialAccountService(settings.CodexRoot, appPaths, processService).SaveCurrent(label);
            Dispatcher.Invoke(() => { OfficialLabelBox.Clear(); RefreshConnections(); RefreshDashboard(); });
        }, cancellationToken), showProgress: false);
    }

    private async void PrepareLogin_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("工具会保存当前档案和恢复点，然后移除活动 auth.json。之后请重新打开 Codex 登录新账号。", "准备登录新账号", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        if (!await EnsureCodexStoppedAsync()) return;
        if (await RunOperationAsync("准备登录", cancellationToken => Task.Run(() => new OfficialAccountService(settings.CodexRoot, appPaths, processService).PrepareNewLogin(), cancellationToken), showProgress: false))
        {
            RefreshConnections(); RefreshDashboard();
        }
    }

    private void SaveApi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tag = (ApiKindBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "CustomApi";
            var kind = Enum.Parse<ConnectionKind>(tag);
            new ApiProviderService(settings.CodexRoot, appPaths, processService).SaveProfile(ApiLabelBox.Text, kind, ApiUrlBox.Text, ApiModelBox.Text, ApiKeyBox.Password);
            ApiKeyBox.Clear(); RefreshConnections(); RefreshDashboard();
            MessageBox.Show("API 档案已使用 DPAPI 加密保存。", "保存完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void SwitchConnection_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsGrid.SelectedItem is not ConnectionProfile profile) { MessageBox.Show("请选择一个连接档案。"); return; }
        if (MessageBox.Show($"切换到“{profile.Label}”？切换前将安全退出 Codex。", "确认切换", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!await EnsureCodexStoppedAsync()) return;
        var success = await RunOperationAsync("安全切换", cancellationToken => Task.Run(() =>
        {
            if (profile.Kind == ConnectionKind.OfficialAccount)
                new OfficialAccountService(settings.CodexRoot, appPaths, processService).SwitchTo(profile.Id);
            else
                new ApiProviderService(settings.CodexRoot, appPaths, processService).SwitchTo(profile.Id, FindCredentialHelper());
        }, cancellationToken), showProgress: false);
        if (success)
        {
            RefreshConnections(); RefreshDashboard();
            MessageBox.Show("切换已完成。请重新打开 Codex。", "安全切换", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void TestApi_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsGrid.SelectedItem is not ConnectionProfile { Kind: ConnectionKind.CustomApi or ConnectionKind.Sub2Api } profile) { MessageBox.Show("请选择普通 API 或 Sub2API 档案。"); return; }
        await RunOperationAsync("测试 API", async cancellationToken =>
        {
            var message = await new ApiProviderService(settings.CodexRoot, appPaths, processService).TestAsync(profile.Id, cancellationToken);
            await Dispatcher.InvokeAsync(() => { RefreshConnections(); MessageBox.Show(message, "检测通过", MessageBoxButton.OK, MessageBoxImage.Information); });
        });
    }

    private async void VerifyOfficialAccount_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsGrid.SelectedItem is not ConnectionProfile { Kind: ConnectionKind.OfficialAccount } profile)
        {
            MessageBox.Show("请选择一个官方账号；普通 API 请使用“测试 API”。", "检测账号", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunOperationAsync("查询官方账号额度", async cancellationToken =>
        {
            var accounts = new OfficialAccountService(settings.CodexRoot, appPaths, processService);
            var usage = await new OfficialAccountVerificationService(accounts).VerifyAsync(profile.Id, cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                RefreshConnections();
                MessageBox.Show("账号可用。\n" + usage.Summary + (string.IsNullOrWhiteSpace(usage.Plan) ? string.Empty : "\n套餐：" + usage.Plan), "官方账号检测", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        });
    }

    private async void VerifyAllOfficialAccounts_Click(object sender, RoutedEventArgs e)
    {
        var profiles = new OfficialAccountService(settings.CodexRoot, appPaths, processService).LoadIndex().Profiles.Where(item => item.Kind == ConnectionKind.OfficialAccount).ToList();
        if (profiles.Count == 0) { MessageBox.Show("还没有已保存的官方账号。", "刷新全部账号", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        await RunOperationAsync("刷新全部官方账号", async cancellationToken =>
        {
            var accounts = new OfficialAccountService(settings.CodexRoot, appPaths, processService);
            var verifier = new OfficialAccountVerificationService(accounts);
            var failures = new List<string>();
            var progress = CreateProgress();
            for (var index = 0; index < profiles.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { await verifier.VerifyAsync(profiles[index].Id, cancellationToken); }
                catch (Exception ex) { failures.Add(profiles[index].Label + "：" + ex.Message); }
                progress.Report(new OperationProgress("刷新官方账号", profiles[index].Label, index + 1, profiles.Count, 0, "正在检测"));
            }
            await Dispatcher.InvokeAsync(() =>
            {
                RefreshConnections();
                MessageBox.Show(failures.Count == 0 ? $"已完成 {profiles.Count} 个账号检测。" : $"已完成检测；失败 {failures.Count} 个。\n" + string.Join("\n", failures), "刷新全部账号", MessageBoxButton.OK, failures.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            });
        });
    }

    private async void DeleteConnection_Click(object sender, RoutedEventArgs e)
    {
        var selected = ConnectionsGrid.SelectedItems.Cast<ConnectionProfile>().ToList();
        if (selected.Count == 0) { MessageBox.Show("请先选择要删除的连接。", "删除连接", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var activeOfficial = selected.Any(item => item.Kind == ConnectionKind.OfficialAccount && item.IsActive);
        var notice = activeOfficial ? "其中包含当前官方账号。删除后会保存恢复副本，并清除 Codex 当前 auth.json，需要重新登录或切换其他账号。\n\n" : string.Empty;
        if (MessageBox.Show(notice + "确认删除所选 " + selected.Count + " 个连接吗？删除前会保留本机加密恢复副本。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!await EnsureCodexStoppedAsync()) return;
        var success = await RunOperationAsync("删除连接", cancellationToken => Task.Run(() =>
        {
            var accounts = new OfficialAccountService(settings.CodexRoot, appPaths, processService);
            var providers = new ApiProviderService(settings.CodexRoot, appPaths, processService);
            var failures = new List<string>();
            foreach (var profile in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (profile.Kind == ConnectionKind.OfficialAccount) accounts.DeleteProfile(profile.Id);
                    else providers.DeleteProfile(profile.Id);
                }
                catch (Exception ex) { failures.Add(profile.Label + "：" + ex.Message); }
            }
            if (failures.Count > 0) throw new InvalidOperationException("部分连接未删除：\n" + string.Join("\n", failures));
        }, cancellationToken), showProgress: false);
        if (success) { RefreshConnections(); RefreshDashboard(); }
    }

    private async void StopCodex_Click(object sender, RoutedEventArgs e) => await EnsureCodexStoppedAsync(reportWhenAlreadyStopped: true);

    private async Task<bool> EnsureCodexStoppedAsync(bool reportWhenAlreadyStopped = false)
    {
        var running = processService.GetRunningProcesses();
        if (running.Count == 0)
        {
            if (reportWhenAlreadyStopped) MessageBox.Show("未检测到正在运行的 Codex。", "安全状态", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        if (MessageBox.Show("检测到：\n" + string.Join("\n", running.Select(item => $"{item.Name}.exe (PID {item.Id})")) + "\n\n是否先正常退出，4 秒后仍未结束时强制关闭？", "退出 Codex", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        try { await processService.StopAllAsync(forceAfterGracePeriod: true); return true; }
        catch (Exception ex) { ShowError(ex); return false; }
    }

    private async void ChooseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择包含开发项目的工作区根目录", InitialDirectory = Directory.Exists(WorkspaceRootBox.Text) ? WorkspaceRootBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(this) != true) return;
        WorkspaceRootBox.Text = dialog.FolderName;
        if (!settings.WorkspaceRoots.Contains(dialog.FolderName, StringComparer.OrdinalIgnoreCase)) settings.WorkspaceRoots.Add(dialog.FolderName);
        settingsService.Save(settings);
        projects = await projectDiscovery.DiscoverAsync(settings.WorkspaceRoots, settings.ProtectedProjectPaths);
        ProjectsGrid.ItemsSource = projects;
        RefreshDashboard();
    }

    private async void ProtectProjects_Click(object sender, RoutedEventArgs e)
    {
        foreach (var project in ProjectsGrid.SelectedItems.OfType<ProjectInfo>())
            if (!settings.ProtectedProjectPaths.Contains(project.Path, StringComparer.OrdinalIgnoreCase)) settings.ProtectedProjectPaths.Add(project.Path);
        settingsService.Save(settings);
        projects = await projectDiscovery.DiscoverAsync(settings.WorkspaceRoots, settings.ProtectedProjectPaths);
        ProjectsGrid.ItemsSource = projects; RefreshDashboard();
    }

    private async void UnprotectProjects_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProjectsGrid.SelectedItems.OfType<ProjectInfo>().Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        settings.ProtectedProjectPaths.RemoveAll(selected.Contains);
        settingsService.Save(settings);
        projects = await projectDiscovery.DiscoverAsync(settings.WorkspaceRoots, settings.ProtectedProjectPaths);
        ProjectsGrid.ItemsSource = projects; RefreshDashboard();
    }

    private void ChooseRepository_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择或创建 Codex Helper 备份仓库目录", InitialDirectory = Directory.Exists(settings.BackupRepositoryPath) ? settings.BackupRepositoryPath : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            PathSafety.EnsureRepositoryOutsideSources(dialog.FolderName, BuildBackupSources().Select(item => item.Path));
            settings.BackupRepositoryPath = dialog.FolderName;
            settingsService.Save(settings);
            new BackupRepository(dialog.FolderName).Initialize();
            RepositoryPathText.Text = dialog.FolderName;
            RefreshSnapshots(); RefreshDashboard();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ExportBundle_Click(object sender, RoutedEventArgs e)
    {
        var items = BuildBundleItems();
        var includeConnections = ExportConnectionsCheck.IsChecked == true;
        var connectionCount = new OfficialAccountService(settings.CodexRoot, appPaths, processService).LoadIndex().Profiles.Count;
        if (items.Count == 0 && (!includeConnections || connectionCount == 0)) { MessageBox.Show("没有选中可导出的数据。"); return; }
        var dialog = new SaveFileDialog { Title = "保存 Codex Helper 批量迁移包", Filter = "Codex Helper 迁移包 (*.chbundle)|*.chbundle", FileName = $"CodexHelper-Transfer-{DateTime.Now:yyyyMMdd-HHmm}.chbundle", AddExtension = true, DefaultExt = ".chbundle" };
        if (dialog.ShowDialog(this) != true) return;
        var password = ExportPasswordBox.Password;
        if (password.Length < 10)
        {
            MessageBox.Show("迁移口令至少需要 10 个字符。请在“迁移口令”输入框补充后再导出。", "迁移口令太短", MessageBoxButton.OK, MessageBoxImage.Information);
            ExportPasswordBox.Focus();
            return;
        }
        await RunOperationAsync("批量导出", async cancellationToken =>
        {
            BundleManifest manifest;
            if (includeConnections)
            {
                var accounts = new OfficialAccountService(settings.CodexRoot, appPaths, processService);
                var providers = new ApiProviderService(settings.CodexRoot, appPaths, processService);
                manifest = await new ConnectionTransferService(appPaths, accounts, providers).ExportAsync(dialog.FileName, password, items, progress: CreateProgress(), cancellationToken: cancellationToken);
            }
            else
            {
                manifest = await new BundleService(appPaths).ExportAsync(new BundleExportRequest(dialog.FileName, password, items), CreateProgress(), cancellationToken);
            }
            await Dispatcher.InvokeAsync(() => MessageBox.Show($"已导出 {manifest.Items.Count} 类数据、{manifest.Files.Count} 个文件。\n{dialog.FileName}", "批量导出完成", MessageBoxButton.OK, manifest.Issues.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning));
        });
    }

    private async void ExportOfficialJson_Click(object sender, RoutedEventArgs e)
    {
        var accounts = new OfficialAccountService(settings.CodexRoot, appPaths, processService);
        if (!accounts.LoadIndex().Profiles.Any(item => item.Kind == ConnectionKind.OfficialAccount))
        {
            MessageBox.Show("还没有已保存的官方账号。请先在“连接中心”保存当前官方登录。", "没有可导出的账号", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var tag = (AccountExportFormatBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? nameof(OfficialAccountExportFormat.OfficialCodexJson);
        var format = Enum.Parse<OfficialAccountExportFormat>(tag);
        var formatName = format switch { OfficialAccountExportFormat.CpaJson => "CPA Codex", OfficialAccountExportFormat.Sub2ApiJson => "Sub2API", _ => "官方 Codex" };
        if (MessageBox.Show("将导出为未加密的 " + formatName + " 账号 JSON，其中包含登录令牌。只应保存到你信任的本地目录或加密介质，且不要发送给他人。继续吗？", "导出账号 JSON", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var dialog = new OpenFolderDialog { Title = "选择存放 " + formatName + " 账号 JSON 的目录", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperationAsync("导出官方账号 JSON", cancellationToken => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = accounts.ExportProfiles(dialog.FolderName, format);
            var files = result.Paths.Count == 1 ? "已生成 1 个批量文件" : "已生成 " + result.Paths.Count + " 个账号文件";
            Dispatcher.Invoke(() => MessageBox.Show($"已按 {formatName} 格式导出 {result.ExportedCount} 个账号，{files}：\n{dialog.FolderName}", "JSON 导出完成", MessageBoxButton.OK, MessageBoxImage.Information));
        }, cancellationToken), showProgress: false);
    }

    private List<BundleExportItem> BuildBundleItems()
    {
        var result = new List<BundleExportItem>();
        if (ExportSkillsCheck.IsChecked == true)
        {
            var skills = Path.Combine(settings.CodexRoot, "skills");
            if (Directory.Exists(skills)) result.Add(new BundleExportItem("skills", "skills", "个人 Skills", skills));
        }
        if (ExportConfigCheck.IsChecked == true)
        {
            var root = settings.CodexRoot;
            foreach (var file in new[] { "config.toml", "AGENTS.md", "hooks.json" }.Select(name => Path.Combine(root, name)).Where(File.Exists))
                result.Add(new BundleExportItem("config-" + ShortHash(file), "codex-config", Path.GetFileName(file), file));
            if (Directory.Exists(root))
                foreach (var file in Directory.EnumerateFiles(root, "*.config.toml", SearchOption.TopDirectoryOnly)) result.Add(new BundleExportItem("profile-" + ShortHash(file), "codex-config", Path.GetFileName(file), file));
        }
        if (ExportProjectsCheck.IsChecked == true)
            foreach (var project in settings.ProtectedProjectPaths.Where(Directory.Exists)) result.Add(new BundleExportItem("project-" + ShortHash(project), "project", Path.GetFileName(project), project));
        return result;
    }

    private void ChooseBundle_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 Codex Helper 迁移包", Filter = "Codex Helper 迁移包 (*.chbundle)|*.chbundle" };
        if (dialog.ShowDialog(this) == true) { ImportBundlePathBox.Text = dialog.FileName; BundlePreviewText.Text = "已选择迁移包，点击“预览”验证口令和内容。"; }
    }

    private async void PreviewBundle_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(ImportBundlePathBox.Text)) { MessageBox.Show("请选择迁移包。"); return; }
        await RunOperationAsync("验证迁移包", async cancellationToken =>
        {
            var preview = await new BundleService(appPaths).PreviewAsync(ImportBundlePathBox.Text, ImportPasswordBox.Password, cancellationToken);
            var connectionCount = preview.Manifest.Items.Count(item => item.Category == ConnectionTransferService.Category);
            await Dispatcher.InvokeAsync(() => BundlePreviewText.Text = $"创建于 {preview.Manifest.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}，来自 {preview.Manifest.DeviceName}，包含 {preview.Manifest.Items.Count} 类数据、{preview.Manifest.Files.Count} 个文件，其中连接档案 {connectionCount} 个。" );
        });
    }

    private async void ImportBundle_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(ImportBundlePathBox.Text)) { MessageBox.Show("请选择迁移包。"); return; }
        var dialog = new OpenFolderDialog { Title = "选择新目录接收批量导入内容" };
        if (dialog.ShowDialog(this) != true) return;
        await RunOperationAsync("批量导入", async cancellationToken =>
        {
            var preview = await new BundleService(appPaths).PreviewAsync(ImportBundlePathBox.Text, ImportPasswordBox.Password, cancellationToken);
            var selectedIds = preview.Manifest.Items.Where(item => item.Category != ConnectionTransferService.Category).Select(item => item.Id).ToList();
            if (selectedIds.Count == 0) throw new InvalidOperationException("迁移包只包含连接档案，请使用“导入连接档案”。");
            var result = await new BundleService(appPaths).ImportAsync(new BundleImportRequest(ImportBundlePathBox.Text, ImportPasswordBox.Password, dialog.FolderName, selectedIds), CreateProgress(), cancellationToken);
            await Dispatcher.InvokeAsync(() => MessageBox.Show($"已导入 {result.ImportedFiles} 个文件到：\n{dialog.FolderName}\n结果：{result.Outcome}", "批量导入完成", MessageBoxButton.OK, result.Outcome == OperationOutcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning));
        });
    }

    private async void ImportConnections_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(ImportBundlePathBox.Text)) { MessageBox.Show("请选择迁移包。"); return; }
        if (MessageBox.Show("连接档案会导入本机加密保险库，但不会自动切换当前登录或 API。继续吗？", "导入连接档案", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        var success = await RunOperationAsync("导入连接档案", async cancellationToken =>
        {
            var accounts = new OfficialAccountService(settings.CodexRoot, appPaths, processService);
            var providers = new ApiProviderService(settings.CodexRoot, appPaths, processService);
            var count = await new ConnectionTransferService(appPaths, accounts, providers).ImportAsync(ImportBundlePathBox.Text, ImportPasswordBox.Password, cancellationToken);
            await Dispatcher.InvokeAsync(() => MessageBox.Show($"已导入 {count} 个连接档案，当前连接未改变。", "连接档案导入完成", MessageBoxButton.OK, MessageBoxImage.Information));
        }, showProgress: false);
        if (success) { RefreshConnections(); RefreshDashboard(); }
    }

    private async void ImportOfficialJson_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择一个或多个官方 Codex、CPA 或 Sub2API 账号 JSON 文件",
            Filter = "账号 JSON 文件 (*.json)|*.json",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0) return;
        var selectedFiles = dialog.FileNames;
        var success = await RunOperationAsync("导入账号 JSON", cancellationToken => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new OfficialAccountService(settings.CodexRoot, appPaths, processService).ImportProfilesFromJson(selectedFiles);
            Dispatcher.Invoke(() => MessageBox.Show($"已读取 {result.ImportedCount} 个账号条目，其中新增 {result.NewProfiles} 个账号。\n当前登录没有改变；请到“连接中心”手动切换。", "JSON 导入完成", MessageBoxButton.OK, MessageBoxImage.Information));
        }, cancellationToken), showProgress: false);
        if (success) { RefreshConnections(); RefreshDashboard(); }
    }

    private async void ImportLegacy_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择旧版工具数据目录、.codex 目录或其上级目录" };
        if (dialog.ShowDialog(this) != true) return;
        var legacyDirectory = dialog.FolderName;
        var success = await RunOperationAsync("导入旧工具数据", cancellationToken => Task.Run(() =>
        {
            var accountCount = 0;
            var apiCount = 0;
            var notes = new List<string>();
            try { accountCount = new OfficialAccountService(settings.CodexRoot, appPaths, processService).ImportLegacyDirectory(legacyDirectory); }
            catch (Exception ex) { notes.Add("账号：" + ex.Message); }
            try { apiCount = new ApiProviderService(settings.CodexRoot, appPaths, processService).ImportLegacyDirectory(legacyDirectory); }
            catch (Exception ex) { notes.Add("API：" + ex.Message); }
            if (accountCount + apiCount == 0 && notes.Count == 2) throw new InvalidOperationException(string.Join(Environment.NewLine, notes));
            Dispatcher.Invoke(() => MessageBox.Show($"已导入官方账号 {accountCount} 个、API 档案 {apiCount} 个。\n当前连接未改变。" + (notes.Count == 0 ? string.Empty : "\n\n提示：\n" + string.Join("\n", notes)), "旧工具迁移完成", MessageBoxButton.OK, notes.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning));
        }, cancellationToken), showProgress: false);
        if (success) { RefreshConnections(); RefreshDashboard(); }
    }

    private void RunHealth_Click(object sender, RoutedEventArgs e)
    {
        var lines = new List<string> { $"检查时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}", string.Empty };
        lines.Add(Directory.Exists(settings.CodexRoot) ? "✓ Codex 根目录存在" : "✗ Codex 根目录不存在");
        var config = Path.Combine(settings.CodexRoot, "config.toml");
        if (File.Exists(config))
        {
            try { _ = TomlConfigurationDocument.Parse(File.ReadAllLines(config)); lines.Add("✓ config.toml 结构有效"); }
            catch (Exception ex) { lines.Add("✗ config.toml：" + ex.Message); }
        }
        else lines.Add("✗ config.toml 不存在");
        var auth = Path.Combine(settings.CodexRoot, "auth.json");
        if (File.Exists(auth))
        {
            try { OfficialAccountService.ValidateAuth(File.ReadAllBytes(auth)); lines.Add("✓ auth.json 结构有效（未显示内容）"); }
            catch (Exception ex) { lines.Add("✗ auth.json：" + ex.Message); }
        }
        else lines.Add("! auth.json 不存在，可能正在使用 API 模式或尚未登录");
        var running = processService.GetRunningProcesses();
        lines.Add(running.Count == 0 ? "✓ Codex 当前未运行，可安全切换或原位恢复" : $"! 检测到 {running.Count} 个 Codex 进程");
        if (!string.IsNullOrWhiteSpace(settings.BackupRepositoryPath))
        {
            try { var snapshots = new BackupRepository(settings.BackupRepositoryPath).ListSnapshots(); lines.Add($"✓ 备份仓库可解锁，共 {snapshots.Count} 个快照"); }
            catch (Exception ex) { lines.Add("✗ 备份仓库：" + ex.Message); }
        }
        else lines.Add("! 尚未设置备份仓库");
        lines.Add($"✓ 已发现 {projects.Count} 个项目，其中 {settings.ProtectedProjectPaths.Count} 个受保护");
        HealthTextBox.Text = string.Join(Environment.NewLine, lines);
    }

    private void ChooseCodexRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 CODEX_HOME", InitialDirectory = Directory.Exists(CodexRootBox.Text) ? CodexRootBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        if (dialog.ShowDialog(this) == true) CodexRootBox.Text = dialog.FolderName;
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(CodexRootBox.Text)) throw new DirectoryNotFoundException("Codex 根目录不存在。");
            settings.CodexRoot = CodexRootBox.Text;
            settings.IncludeSessions = IncludeSessionsCheck.IsChecked == true;
            settings.IncludeAttachments = IncludeAttachmentsCheck.IsChecked == true;
            settings.IncludeGeneratedImages = IncludeGeneratedCheck.IsChecked == true;
            settingsService.Save(settings);
            await RefreshAllAsync();
            MessageBox.Show("设置已保存并重新扫描。", "设置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowTutorial_Click(object sender, RoutedEventArgs e)
    {
        ShowGuide(markOnboardingComplete: false);
    }

    private void ShowGuide(bool markOnboardingComplete)
    {
        if (guideWindow is { IsLoaded: true })
        {
            guideWindow.Activate();
            return;
        }
        var guide = new WelcomeWindow { Owner = this };
        guideWindow = guide;
        guide.NavigateRequested += (_, page) => SelectPage(page);
        guide.Closed += (_, _) =>
        {
            guideWindow = null;
            if (!markOnboardingComplete) return;
            settings.HasCompletedOnboarding = true;
            settingsService.Save(settings);
        };
        guide.Show();
        guide.NavigateToCurrentStep();
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e) => operationCancellation?.Cancel();

    private async Task<bool> RunOperationAsync(string stage, Func<CancellationToken, Task> operation, bool showProgress = true)
    {
        if (operationRunning) { MessageBox.Show("已有任务正在运行，请等待完成或取消。", "任务进行中", MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        operationRunning = true;
        operationCancellation = new CancellationTokenSource();
        OperationStageText.Text = stage;
        OperationMessageText.Text = "正在准备…";
        OperationProgressBar.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        OperationProgressBar.IsIndeterminate = true;
        CancelOperationButton.Visibility = Visibility.Visible;
        try
        {
            await operation(operationCancellation.Token);
            OperationStageText.Text = "完成";
            OperationMessageText.Text = stage + "已完成。";
            return true;
        }
        catch (OperationCanceledException)
        {
            OperationStageText.Text = "已取消";
            OperationMessageText.Text = "任务已安全取消，已提交的数据保持完整。";
            return false;
        }
        catch (Exception ex)
        {
            OperationStageText.Text = "失败";
            OperationMessageText.Text = ex.Message;
            ShowError(ex);
            return false;
        }
        finally
        {
            operationCancellation.Dispose();
            operationCancellation = null;
            operationRunning = false;
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Visibility = Visibility.Collapsed;
            CancelOperationButton.Visibility = Visibility.Collapsed;
        }
    }

    private IProgress<OperationProgress> CreateProgress() => new Progress<OperationProgress>(value =>
    {
        OperationStageText.Text = value.Stage;
        OperationMessageText.Text = string.IsNullOrWhiteSpace(value.Message) ? value.CurrentItem : value.Message + (string.IsNullOrWhiteSpace(value.CurrentItem) ? string.Empty : " · " + value.CurrentItem);
        if (value.TotalItems > 0)
        {
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = Math.Clamp(value.CompletedItems * 100d / value.TotalItems, 0, 100);
        }
        else OperationProgressBar.IsIndeterminate = true;
    });

    private static string FindCredentialHelper()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDirectory, "CodexHelperCredentialHelper.exe");
        if (File.Exists(direct)) return direct;
        var versioned = Directory.EnumerateFiles(baseDirectory, "codex-helper-credential-helper-v*-windows-x64.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (versioned is not null) return versioned;
        var debug = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "CodexHelper.CredentialHelper", "bin", "Debug", "net8.0-windows", "CodexHelperCredentialHelper.exe"));
        if (File.Exists(debug)) return debug;
        var release = debug.Replace($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}");
        if (File.Exists(release)) return release;
        throw new FileNotFoundException("找不到 Codex Helper 凭据助手，请重新安装或使用完整便携包。");
    }

    private static string ShortHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(value).ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static string FormatBytes(long bytes) => (string)ByteSizeConverter.Instance.Convert(bytes, typeof(string), null!, System.Globalization.CultureInfo.CurrentCulture);

    private void ShowError(Exception ex)
    {
        var errorId = logger.WriteError(OperationStageText.Text, ex);
        MessageBox.Show(ex.Message + $"\n\n错误编号：{errorId}\n详细信息已写入本机日志。", "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

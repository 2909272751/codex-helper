using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using CodexHelper.Core.Infrastructure;
using CodexHelper.Core.Models;
using CodexHelper.Core.Services;

namespace CodexHelper.App;

/// <summary>Reasonix 任务列表的只读后台视图数据；UI 线程只负责把该不可变数据渲染到控件，磁盘读取绝不在 UI 线程发生。</summary>
internal sealed record ReasonixTaskListView(
    ReasonixTasksSnapshot Snapshot,
    IReadOnlyList<ReasonixTaskStatus> Tasks,
    IReadOnlyList<ReasonixTaskDiagnostic> Diagnostics,
    ReasonixSchedulerSnapshot Schedule,
    IReadOnlyDictionary<string, ReasonixTaskDecision> Decisions,
    string? SelectedTaskId,
    IReadOnlyList<ReasonixWorkerStep>? Steps,
    IReadOnlyList<string> WorkerChecks,
    string? RetryBlockReason);

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
    private ReasonixTaskStatus? selectedReasonixTask;
    private string? selectedReasonixTaskId;
    private bool suppressCacheRangeSelection;
    private bool suppressCollaborationModeSelection;
    private readonly ReasonixTaskRefreshCoordinator taskRefreshCoordinator = new();
    private bool suppressReasonixSelection;
    private string? retryBlockReasonForSelected;
    private DeepSeekHarnessStatus? harnessStatus;
    private DeepSeekHarnessService? harnessService;
    private DeepSeekHarnessRunner? harnessRunner;
    private DeepSeekHarnessStartupService? harnessStartupService;
    private bool harnessRefreshInFlight;
    private bool reasonixRefreshInFlight;
    private HarnessTaskStatus? selectedHarnessTask;
    private string? selectedHarnessTaskId;
    private bool suppressHarnessSelection;
    private bool harnessTaskRefreshInFlight;
    private bool harnessTaskRefreshQueued;
    private string? harnessTaskRenderedFingerprint;
    private IReadOnlyList<DshComponentInfo> dshComponents = Array.Empty<DshComponentInfo>();
    private CancellationTokenSource? dshScanCts;

    public MainWindow()
    {
        InitializeComponent();
        // 全局滚轮链：内层 DataGrid/ListBox/只读多行 TextBox 到顶/底或不可滚动时，
        // 把滚轮转发给唯一主 ScrollViewer；内层仍可滚动时保持原生（见 ScrollWheelChain）。
        ScrollWheelChain.Attach(MainScrollViewer);
        VersionText.Text = "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "—");
        logger = new AppLogger(appPaths);
        settingsService = new SettingsService(appPaths);
        settings = settingsService.Load();
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) =>
        {
            dshScanCts?.Cancel();
            taskRefreshCoordinator.Close();
        };
    }

    private async Task InitializeAsync()
    {
        ApplySettingsToUi();
        // 启动时按持久化模式做一次幂等一致性修复：升级后自动补齐 Harness skill/调用脚本，
        // 并清除另一执行器的残留规则；只写本机配置文件，不启动外部执行器，不阻塞 UI 线程。
        await Task.Run(() => new CollaborationService(settings.CodexRoot, appPaths).Synchronize(settings));
        UpdateCollaborationModeUi();
        SelectPage(string.IsNullOrWhiteSpace(settings.LastSelectedPage) ? "Dashboard" : settings.LastSelectedPage);
        if (!settings.HasCompletedOnboarding) ShowGuide(markOnboardingComplete: true);
        // 窗口先进入可操作状态；首次数据盘点不占用全局 operationRunning，
        // 也不在启动阶段调用 Reasonix/Harness 外部进程。
        await Task.Yield();
        _ = RefreshStartupDataAsync();
    }

    private async Task RefreshStartupDataAsync()
    {
        try
        {
            var discoveredInventory = await codexDiscovery.DiscoverAsync(settings);
            var discoveredProjects = await projectDiscovery.DiscoverAsync(settings.WorkspaceRoots, settings.ProtectedProjectPaths);
            if (!IsLoaded || taskRefreshCoordinator.Closed) return;
            inventory = discoveredInventory;
            projects = discoveredProjects;
            RefreshConnections(loadCollaboration: false);
            RefreshSnapshots();
            RefreshDshSnapshots();
            RefreshDshComponents();
            RefreshDashboard();
            InventoryGrid.ItemsSource = inventory;
            ProjectsGrid.ItemsSource = projects;
            UpdateProjectActionButtons();
        }
        catch (Exception ex)
        {
            if (IsLoaded && !taskRefreshCoordinator.Closed)
                OperationMessageText.Text = "后台数据盘点未完成：" + ex.Message;
        }
    }

    private void ApplySettingsToUi()
    {
        CodexRootBox.Text = settings.CodexRoot;
        RepositoryPathText.Text = string.IsNullOrWhiteSpace(settings.BackupRepositoryPath) ? "尚未设置" : settings.BackupRepositoryPath;
        if (DshRepositoryPathText is not null)
            DshRepositoryPathText.Text = string.IsNullOrWhiteSpace(settings.BackupRepositoryPath) ? "尚未设置" : settings.BackupRepositoryPath;
        WorkspaceRootBox.Text = settings.WorkspaceRoots.FirstOrDefault() ?? string.Empty;
        IncludeSessionsCheck.IsChecked = settings.IncludeSessions;
        IncludeAttachmentsCheck.IsChecked = settings.IncludeAttachments;
        IncludeGeneratedCheck.IsChecked = settings.IncludeGeneratedImages;
        SelectReasonixIntensity(settings.ReasonixExecutionIntensity);
        LoadParallelSettings(settings);
        SelectDeepSeekCacheRange(settings.DeepSeekCacheRange);
        SelectComboTag(HarnessExecutionModeBox, HarnessExecutionOptions.NormalizeMode(settings.HarnessExecutionMode));
        SelectComboTag(HarnessPermissionModeBox, HarnessExecutionOptions.NormalizePermission(settings.HarnessPermissionMode));
        SelectComboTag(HarnessExecutionStrengthBox, HarnessExecutionOptions.NormalizeStrength(settings.HarnessExecutionStrength));
        HarnessReuseSessionCheck.IsChecked = settings.HarnessReuseSession;
        HarnessAutoStartHostCheck.IsChecked = settings.HarnessAutoStartHost;
        HarnessReturnToGptCheck.IsChecked = settings.HarnessReturnToGptOnFailure;
        suppressCollaborationModeSelection = true;
        SelectCollaborationMode(settings.CollaborationMode);
        suppressCollaborationModeSelection = false;
        UpdateCollaborationModeUi();
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
                UpdateProjectActionButtons();
                RefreshConnections(loadCollaboration: true);
                RefreshSnapshots();
                RefreshDshSnapshots();
                RefreshDashboard();
            });
        }, showProgress: false);
    }

    private void RefreshConnections(bool loadCollaboration = true)
    {
        try
        {
            var service = new OfficialAccountService(settings.CodexRoot, appPaths, processService);
            ConnectionsGrid.ItemsSource = service.LoadIndex().Profiles.OrderByDescending(item => item.IsActive).ThenBy(item => item.Label).ToList();
            RefreshAccountHealthDetail();
            if (loadCollaboration) _ = RefreshSubagentSettingsAsync();
        }
        catch
        {
            ConnectionsGrid.ItemsSource = Array.Empty<ConnectionProfile>();
        }
        UpdateConnectionActionButtons();
    }

    private void ConnectionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshAccountHealthDetail();
        UpdateConnectionActionButtons();
    }

    /// <summary>连接中心：依赖选择的按钮在无选择时禁用，选择后按档案类型启用（不靠点击后弹错代替禁用态）。</summary>
    private void UpdateConnectionActionButtons()
    {
        var selected = ConnectionsGrid.SelectedItem as ConnectionProfile;
        SwitchConnectionButton.IsEnabled = selected is not null;
        VerifyAccountButton.IsEnabled = selected is { Kind: ConnectionKind.OfficialAccount };
        TestApiButton.IsEnabled = selected is { Kind: ConnectionKind.CustomApi or ConnectionKind.Sub2Api or ConnectionKind.ResponsesSubagent };
        RepairLegacyButton.IsEnabled = selected is { Kind: ConnectionKind.ResponsesSubagent };
        DeleteConnectionButton.IsEnabled = ConnectionsGrid.SelectedItems.Count > 0;
    }

    /// <summary>可等待的 Reasonix 环境刷新：单飞（进行中重复调用直接跳过，绝不并发探测）；
    /// 事件处理器是唯一 async void 边界；窗口关闭后不回写 UI，异常转换为可读状态。</summary>
    private async Task RefreshSubagentSettingsAsync(ReasonixCliSelection? precomputedSelection = null)
    {
        if (SubagentSettingsActionButton is null || !IsLoaded || taskRefreshCoordinator.Closed || reasonixRefreshInFlight) return;
        reasonixRefreshInFlight = true;
        try
        {
            var service = new ReasonixIntegrationService(settings.CodexRoot, appPaths);
            // 单次刷新只做一次候选探测：预计算 selection 提供时直接复用其版本与 doctor 结果，
            // 诊断与模型读取都不再启动进程。
            var selection = precomputedSelection ?? await service.DiscoverBestAsync();
            await Task.Run(() => service.RefreshManagedScripts(selection.Best?.Path));
            var status = await service.DiagnoseAsync(precomputedSelection: selection);
            if (!IsLoaded || taskRefreshCoordinator.Closed) return;
            SubagentSettingsActionButton.Content = status.IntegrationEnabled ? "关闭协作编码" : "开启协作编码";
            SubagentSettingsActionButton.IsEnabled = status.Installed;
            SubagentSettingsStatusText.Text = status.Installed
                ? $"Reasonix {status.Version} · 模型 {status.DefaultModel} · {(status.CredentialReady ? "凭据已保存" : "缺少凭据")} · {(status.IntegrationEnabled ? "协作已开启" : "协作已关闭")}\n{status.CredentialMessage}"
                : status.CredentialMessage;
            UpdateReasonixEnvironment(selection, status);
            SelectReasonixPermissionMode(service.GetPermissionMode());
            var models = await service.GetAvailableModelsAsync(precomputedSelection: selection);
            if (!IsLoaded || taskRefreshCoordinator.Closed) return;
            ReasonixDefaultModelBox.ItemsSource = models;
            ReasonixDefaultModelBox.SelectedItem = models.FirstOrDefault(item => string.Equals(item.Id, status.DefaultModel, StringComparison.OrdinalIgnoreCase));
            RequestReasonixTaskRefresh(manual: true);
        }
        catch (Exception ex)
        {
            if (IsLoaded && !taskRefreshCoordinator.Closed)
            {
                SubagentSettingsActionButton.IsEnabled = false;
                SubagentSettingsStatusText.Text = "Reasonix 检查失败：" + ex.Message;
            }
        }
        finally { reasonixRefreshInFlight = false; }
    }

    /// <summary>执行环境行：当前实际 CLI 路径、版本、来源与协议兼容性；多候选/迁移说明一并展示。</summary>
    private void SelectCollaborationMode(string mode)
    {
        var value = CollaborationModeExtensions.ParseCollaborationMode(mode);
        foreach (var item in CollaborationModeBox.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag?.ToString(), value.ToPersisted(), StringComparison.OrdinalIgnoreCase)) { CollaborationModeBox.SelectedItem = item; return; }
    }

    private CollaborationMode SelectedCollaborationMode()
        => CollaborationModeExtensions.ParseCollaborationMode((CollaborationModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());

    private async void CollaborationModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressCollaborationModeSelection || CollaborationModeBox is null) return;

        var previousMode = settings.CollaborationMode;
        var mode = SelectedCollaborationMode();
        settings.CollaborationMode = mode.ToPersisted();
        UpdateCollaborationModeUi();
        CollaborationModeBox.IsEnabled = false;
        CollaborationModeHintText.Text += " 正在后台应用协作规则…";

        try
        {
            await Task.Run(() =>
            {
                settingsService.Save(settings);
                if (mode == CollaborationMode.Harness)
                {
                    // Harness 协作规则只在“开启协作编码”就绪验证通过后写入；
                    // 此处仅维护执行器互斥（若 Reasonix 协作开启则关闭它），不应用 Harness 规则。
                    var reasonix = new ReasonixIntegrationService(settings.CodexRoot, appPaths);
                    if (reasonix.IsEnabled()) reasonix.Disable();
                }
                else
                {
                    new CollaborationService(settings.CodexRoot, appPaths).Synchronize(settings);
                }
            });

            UpdateCollaborationModeUi();
            if (mode == CollaborationMode.Harness)
            {
                if (HarnessEnvironmentText is not null)
                    HarnessEnvironmentText.Text = "DeepSeek Harness 协作规则将在“开启协作编码”验证就绪后应用。点击“重新检测”检查当前环境。";
                RequestHarnessTaskRefresh(manual: false);
            }
            else if (mode == CollaborationMode.Reasonix && SubagentSettingsStatusText is not null)
                SubagentSettingsStatusText.Text = "Reasonix 协作规则已应用。需要时点击“重新扫描”检查当前环境。";
        }
        catch (Exception ex)
        {
            settings.CollaborationMode = previousMode;
            await Task.Run(() => settingsService.Save(settings));
            suppressCollaborationModeSelection = true;
            SelectCollaborationMode(previousMode);
            suppressCollaborationModeSelection = false;
            UpdateCollaborationModeUi();
            ShowError(ex);
        }
        finally
        {
            CollaborationModeBox.IsEnabled = true;
        }
    }

    private void UpdateCollaborationModeUi()
    {
        var mode = CollaborationModeExtensions.ParseCollaborationMode(settings.CollaborationMode);
        CollaborationModeHintText.Text = mode switch
        {
            CollaborationMode.Reasonix => "已选择 Reasonix 执行器：GPT 规划/验收，Reasonix 实现，维持现有三档策略。",
            CollaborationMode.Harness => "已选择 DeepSeek Harness 执行器：GPT 规划/验收，Harness 实现；点击“开启协作编码”验证环境并应用协作规则。",
            _ => "已关闭协作：移除 Helper 管理的协作规则，GPT 独立开发。"
        };
        // 互斥模式容器：任一时刻只有一个面板可见；Off 只显示简洁说明，不显示任何执行器检测按钮。
        CollaborationOffPanel.Visibility = mode == CollaborationMode.Off ? Visibility.Visible : Visibility.Collapsed;
        CollaborationHarnessPanel.Visibility = mode == CollaborationMode.Harness ? Visibility.Visible : Visibility.Collapsed;
        CollaborationReasonixPanel.Visibility = mode == CollaborationMode.Reasonix ? Visibility.Visible : Visibility.Collapsed;
        UpdateHarnessCollaborationState();
    }

    /// <summary>主协作卡状态：按“协作规则是否已应用”与最近一次环境诊断结果刷新徽标、按钮与状态文案。
    /// 规则只在就绪验证通过后应用；失败时保持之前的安全状态并给出可读原因。
    /// 仅在 Harness 面板激活时读取规则文件（面板隐藏时控件不可见，无需读取）。</summary>
    private void UpdateHarnessCollaborationState()
    {
        if (HarnessCollaborationActionButton is null || HarnessModeBadge is null || HarnessCollaborationStatusText is null) return;
        if (CollaborationModeExtensions.ParseCollaborationMode(settings.CollaborationMode) != CollaborationMode.Harness) return;
        var enabled = new CollaborationService(settings.CodexRoot, appPaths).IsHarnessEnabled();
        HarnessCollaborationActionButton.Content = enabled ? "关闭协作编码" : "开启协作编码";
        HarnessModeBadge.Text = enabled ? "协作已开启" : "协作未开启";
        if (enabled)
        {
            HarnessCollaborationStatusText.Text = "Helper 管理的 Harness 协作规则已应用：GPT 规划/验收，Harness 实现。";
        }
        else if (harnessStatus is null)
        {
            HarnessCollaborationStatusText.Text = "协作未开启。点击“开启协作编码”先验证环境（Node/dsh/Web Host/中继），就绪后应用协作规则。";
        }
        else if (harnessStatus.RelayConfirmed)
        {
            HarnessCollaborationStatusText.Text = "环境就绪：Node、dsh、Web Host 与自动中继均已确认。点击“开启协作编码”应用协作规则。";
        }
        else if (harnessStatus.WebHostRunning)
        {
            HarnessCollaborationStatusText.Text = "Web 可用，但自动协作中继不可用。" + harnessStatus.RelayMessage;
        }
        else
        {
            var reason = FirstNonEmpty(harnessStatus.NodeMessage, harnessStatus.DshMessage, harnessStatus.DiscoveryNote ?? string.Empty);
            HarnessCollaborationStatusText.Text = "环境未就绪：" + (string.IsNullOrWhiteSpace(reason) ? "请点击“重新检测”查看详情。" : reason);
        }
    }

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    /// <summary>可等待的 Harness 环境检测：单飞（进行中重复调用直接跳过，绝不并发探测）；
    /// 检测期间禁用“重新检测”按钮并显示“检测中”，结束后恢复；外部诊断命令有界超时且支持取消；
    /// 窗口关闭后不回写 UI，取消/异常转换为可读状态。</summary>
    private async Task RefreshHarnessSettingsAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (HarnessEnvironmentText is null || !IsLoaded || taskRefreshCoordinator.Closed || harnessRefreshInFlight) return;
        harnessRefreshInFlight = true;
        if (HarnessRescanButton is not null) { HarnessRescanButton.IsEnabled = false; HarnessRescanButton.Content = "检测中…"; }
        try
        {
            harnessService ??= new DeepSeekHarnessService(appPaths);
            var status = await harnessService.DiagnoseAsync(settings.HarnessNodePath, settings.HarnessDshEntryPath, cancellationToken, forceRefresh);
            if (!IsLoaded || taskRefreshCoordinator.Closed) return;
            harnessStatus = status;
            var riskText = status.DshRisk == HarnessVersionRiskLevel.Invalid
                ? "非法/损坏"
                : DeepSeekHarnessSemVer.Describe(status.DshRisk);
            var statusKindText = status.StatusKind switch
            {
                HarnessStatusKind.Usable => "可使用",
                HarnessStatusKind.WebOnly => "可打开 Web 但自动中继未确认",
                HarnessStatusKind.NewVersionVerified => "新版本已验证",
                HarnessStatusKind.NewVersionFailed => "新版本未通过",
                HarnessStatusKind.Broken => "安装损坏",
                _ => "未知"
            };
            var lines = new List<string>
            {
                $"Node：{(status.NodeFound ? $"{status.NodeVersion}（{status.NodeSource}）" : "未找到")} · 版本规则：{(status.NodeVersionSupported ? "满足" : "不满足")}",
                $"Harness：{(status.DshFound ? $"{status.DshVersion}（{status.DshSource} · {riskText}）" : "未找到")}",
                $"状态：{statusKindText}",
                $"Web Host：{(status.WebHostRunning ? $"运行中（{status.WebUrl}）" : "未运行")}",
                $"合同 profile/patch：{(status.CapabilityContractProfile ? "可用" : "未确认")}——{status.ContractProfileMessage}",
                $"权限环境：{(status.CapabilityPermissionEnv ? "可注入" : "未生效")}——{status.PermissionEnvMessage}",
                $"模型思考强度：{HarnessExecutionOptions.ModelReasoningText}",
                status.NodeMessage,
                status.DshMessage,
                status.RelayMessage
            };
            if (status.DshRisk == HarnessVersionRiskLevel.CrossMajor)
                lines.Add("警告：dsh 为跨主版本新版本，尚未验证，请确认能力探测通过后再用于正式任务。");
            HarnessEnvironmentText.Text = string.Join(Environment.NewLine, lines);
            HarnessEnvironmentText.ToolTip = HarnessEnvironmentText.Text;
            OpenHarnessWebButton.IsEnabled = status.WebHostRunning || status.EnableAllowed;
            OpenHarnessWebButton.Content = status.WebHostRunning ? "打开 Harness Web" : "启动并打开 Harness Web";
            harnessStartupService ??= new DeepSeekHarnessStartupService(appPaths);
            HarnessStartupStatusText.Text = (await harnessStartupService.GetStatusAsync(status.NodePath, status.DshEntryPath, cancellationToken)).Message;
            UpdateCollaborationModeUi();
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded && !taskRefreshCoordinator.Closed)
                HarnessEnvironmentText.Text = "Harness 检测已取消。";
        }
        catch (Exception ex)
        {
            if (IsLoaded && !taskRefreshCoordinator.Closed)
                HarnessEnvironmentText.Text = "Harness 检查失败：" + ex.Message;
        }
        finally
        {
            if (IsLoaded && !taskRefreshCoordinator.Closed && HarnessRescanButton is not null)
            {
                HarnessRescanButton.IsEnabled = true;
                HarnessRescanButton.Content = "重新检测";
            }
            harnessRefreshInFlight = false;
        }
    }

    private async void OpenHarnessWeb_Click(object sender, RoutedEventArgs e)
    {
        if (harnessStatus is null) { _ = RefreshHarnessSettingsAsync(); return; }
        if (harnessStatus.WebHostRunning)
        {
            Process.Start(new ProcessStartInfo(harnessStatus.WebUrl) { UseShellExecute = true });
            return;
        }
        if (harnessStatus.EnableAllowed && harnessService is not null)
        {
            OpenHarnessWebButton.IsEnabled = false;
            OpenHarnessWebButton.Content = "正在启动…";
            try
            {
                var ready = await harnessService.EnsureWebHostReadyAsync(settings.HarnessNodePath, settings.HarnessDshEntryPath);
                harnessStatus = ready.Status;
                await RefreshHarnessSettingsAsync(forceRefresh: true);
                if (ready.Ready)
                {
                    Process.Start(new ProcessStartInfo(ready.Status.WebUrl) { UseShellExecute = true });
                    return;
                }
                MessageBox.Show(ready.Message, "Harness Host", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                if (IsLoaded)
                    OpenHarnessWebButton.IsEnabled = harnessStatus?.EnableAllowed == true || harnessStatus?.WebHostRunning == true;
            }
            return;
        }
        MessageBox.Show("未检测到可用的 Harness Web Host，且 Node 环境未就绪。\n" + DeepSeekHarnessVersions.NodeDownloadUrl, "Harness", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void HarnessRescan_Click(object sender, RoutedEventArgs e)
        => await RunOperationAsync("重新检测 Harness", cancellationToken => RefreshHarnessSettingsAsync(forceRefresh: true, cancellationToken), showProgress: false);

    /// <summary>启动 Web Host 后延迟刷新检测结果；窗口关闭后不再回写 UI。</summary>
    private async Task RefreshHarnessAfterStartAsync()
    {
        await Task.Delay(1200);
        if (!IsLoaded || taskRefreshCoordinator.Closed) return;
        await RefreshHarnessSettingsAsync();
    }

    private void StopHarnessHost_Click(object sender, RoutedEventArgs e)
    {
        if (harnessService is null) return;
        harnessService.StopWebHost();
        _ = RefreshHarnessSettingsAsync();
        MessageBox.Show("已停止 Helper 启动的 Harness Web Host。", "Harness Host", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ChooseHarnessNode_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Node.js 可执行文件（node.exe）",
            Filter = "Node.js (node.exe)|node.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        settings.HarnessNodePath = dialog.FileName;
        settingsService.Save(settings);
        _ = RefreshHarnessSettingsAsync();
    }

    private void OpenHarnessWebSettings_Click(object sender, RoutedEventArgs e) => OpenHarnessWeb_Click(sender, e);

    private static string SelectedComboTag(ComboBox box, string fallback)
        => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectComboTag(ComboBox box, string value)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            { box.SelectedItem = item; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private async void ApplyHarnessExecutionSettings_Click(object sender, RoutedEventArgs e)
    {
        var success = await RunOperationAsync("应用 Harness 合同设置", async cancellationToken =>
        {
            settings.HarnessExecutionMode = HarnessExecutionOptions.NormalizeMode(SelectedComboTag(HarnessExecutionModeBox, HarnessExecutionOptions.DefaultMode));
            settings.HarnessPermissionMode = HarnessExecutionOptions.NormalizePermission(SelectedComboTag(HarnessPermissionModeBox, HarnessExecutionOptions.DefaultPermission));
            settings.HarnessExecutionStrength = HarnessExecutionOptions.NormalizeStrength(SelectedComboTag(HarnessExecutionStrengthBox, HarnessExecutionOptions.DefaultStrength));
            settings.HarnessReuseSession = HarnessReuseSessionCheck.IsChecked == true;
            settings.HarnessAutoStartHost = HarnessAutoStartHostCheck.IsChecked == true;
            settings.HarnessReturnToGptOnFailure = HarnessReturnToGptCheck.IsChecked == true;
            settingsService.Save(settings);

            harnessService ??= new DeepSeekHarnessService(appPaths);
            var status = await harnessService.DiagnoseAsync(settings.HarnessNodePath, settings.HarnessDshEntryPath, cancellationToken, forceRefresh: true);
            if (!status.EnableAllowed) throw new InvalidOperationException(FirstNonEmpty(status.NodeMessage, status.DshMessage, "Harness 环境未就绪。"));
            settings.HarnessNodePath = status.NodePath;
            settings.HarnessDshEntryPath = status.DshEntryPath;
            var profileDegraded = false;
            if (settings.HarnessExecutionMode == HarnessExecutionOptions.DefaultMode)
            {
                // 合同 profile 幂等生成；结构不兼容/无法定位时记录降级，UI 不虚报 codex-contract。
                // Runner 启动时同样探测，安装失败会诚实使用 standard 预设并继续。
                try
                {
                    await Task.Run(() => new HarnessContractProfileService().InstallOrRepair(status.DshEntryPath), cancellationToken);
                }
                catch
                {
                    profileDegraded = true;
                }
            }
            settingsService.Save(settings);

            // 权限是 Host 进程环境，应用后重启 Helper 管理的 Host 才能保证新会话真正使用新权限。
            harnessService.StopWebHost();
            if (settings.HarnessAutoStartHost)
            {
                var ready = await harnessService.EnsureWebHostReadyAsync(status.NodePath, status.DshEntryPath, cancellationToken, settings.HarnessPermissionMode);
                if (!ready.Ready) throw new InvalidOperationException(ready.Message);
            }
            if (profileDegraded)
                await Dispatcher.InvokeAsync(() => MessageBox.Show("codex-contract 预设无法生成（当前 Harness 结构不兼容），合同模式将降级为 standard；合同边界仍由短提示保证。", "已降级", MessageBoxButton.OK, MessageBoxImage.Warning));
            await RefreshHarnessSettingsAsync(forceRefresh: true, cancellationToken);
        }, showProgress: false);
        if (success) MessageBox.Show("Harness 合同设置已应用。完全控制通过 Host 受控环境传递，不会写入命令行或日志。", "设置完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RestoreHarnessRecommendedSettings_Click(object sender, RoutedEventArgs e)
    {
        HarnessExecutionOptions.RestoreRecommended(settings);
        settingsService.Save(settings);
        ApplySettingsToUi();
        MessageBox.Show("已恢复推荐设置：Codex 合同模式、完全控制、标准执行强度、会话复用和自动启动。请点“应用并测试”使 Host 权限生效。", "已恢复", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Harness 主协作卡动作：开启时先验证 Node/dsh/Web Host/中继能力，Host 未运行自动启动，
    /// 全部就绪后才应用 Helper 管理的协作规则；任何一步失败都保持之前的安全状态并给出可读原因。
    /// 关闭时只移除 Helper 管理的 Harness 规则，不删除 Harness、Node、模型凭据或其他 Skills。
    /// </summary>
    private async void HarnessCollaborationActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCollaborationMode() != CollaborationMode.Harness) return;
        var collaboration = new CollaborationService(settings.CodexRoot, appPaths);
        var enabled = collaboration.IsHarnessEnabled();
        var action = enabled ? "关闭协作编码" : "开启协作编码";
        var explanation = enabled
            ? "将移除 Helper 管理的 Harness 执行 Skill 与全局协作规则，GPT 恢复独立开发。不会删除 Harness、Node、模型凭据或其他 Skills。"
            : "将先验证 Node、dsh、Web Host 与自动中继能力（提交/事件流/取消）；Host 未运行时自动启动。全部就绪后才应用 Helper 管理的 Harness 协作规则（GPT 规划/验收，Harness 实现）。";
        if (MessageBox.Show(explanation + "\n\n继续吗？", action, MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;

        var success = await RunOperationAsync(action, async cancellationToken =>
        {
            if (enabled)
            {
                await Task.Run(() => collaboration.RemoveHarness(), cancellationToken);
                return;
            }
            // 1) 环境与入口验证（Node/dsh/Web profile）。
            harnessService ??= new DeepSeekHarnessService(appPaths);
            var status = await harnessService.DiagnoseAsync(settings.HarnessNodePath, settings.HarnessDshEntryPath, cancellationToken, forceRefresh: true);
            if (!status.EnableAllowed)
            {
                var reason = FirstNonEmpty(status.NodeMessage, status.DshMessage, status.DiscoveryNote ?? string.Empty, "Node 或 dsh 入口未就绪。");
                throw new InvalidOperationException(reason);
            }
            // 2) Host 未运行 → 自动启动并等待健康检查。
            if (!status.WebHostRunning)
            {
                var ready = await harnessService.EnsureWebHostReadyAsync(settings.HarnessNodePath, settings.HarnessDshEntryPath, cancellationToken);
                if (!ready.Ready) throw new InvalidOperationException("无法启动 Harness Web Host：" + ready.Message);
                status = ready.Status;
            }
            // 3) 中继能力确认（提交/事件流/取消三项），未确认绝不应用规则。
            if (!status.RelayConfirmed)
                throw new InvalidOperationException("Web 可用，但自动协作中继不可用。" + status.RelayMessage);
            // 4) 就绪后才应用协作规则；失败路径不会走到这里，保持之前的安全状态。
            await Task.Run(() => collaboration.Synchronize(settings), cancellationToken);
        }, showProgress: false);

        if (success)
        {
            UpdateCollaborationModeUi();
            RequestHarnessTaskRefresh(manual: true);
            MessageBox.Show(enabled ? "已关闭。GPT 将独立完成任务。" : "已开启。环境就绪且协作规则已应用：GPT 规划、Harness 实现、GPT 验收。", "设置完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void ConfigureHarnessStartup_Click(object sender, RoutedEventArgs e)
    {
        var success = await RunOperationAsync("一键配置 Codex + Harness", async cancellationToken =>
        {
            harnessService ??= new DeepSeekHarnessService(appPaths);
            var status = await harnessService.DiagnoseAsync(settings.HarnessNodePath, settings.HarnessDshEntryPath, cancellationToken, forceRefresh: true);
            if (!status.EnableAllowed) throw new InvalidOperationException(FirstNonEmpty(status.NodeMessage, status.DshMessage, "Node 或 Harness 未就绪。"));
            settings.HarnessNodePath = status.NodePath;
            settings.HarnessDshEntryPath = status.DshEntryPath;
            settings.CollaborationMode = CollaborationMode.Harness.ToPersisted();
            settingsService.Save(settings);
            await Task.Run(() => new CollaborationService(settings.CodexRoot, appPaths).Synchronize(settings), cancellationToken);
            harnessStartupService ??= new DeepSeekHarnessStartupService(appPaths);
            await harnessStartupService.ConfigureAsync(status.NodePath, status.DshEntryPath, cancellationToken: cancellationToken);
            var ready = await harnessService.EnsureWebHostReadyAsync(status.NodePath, status.DshEntryPath, cancellationToken);
            if (!ready.Ready) throw new InvalidOperationException(ready.Message);
            HarnessStartupStatusText.Text = (await harnessStartupService.GetStatusAsync(status.NodePath, status.DshEntryPath, cancellationToken)).Message;
        }, showProgress: false);
        if (success)
        {
            UpdateCollaborationModeUi();
            MessageBox.Show("已配置 Codex 协作规则、Harness 登录自启动和当前 Host。Helper 无需常驻。", "配置完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void RemoveHarnessStartup_Click(object sender, RoutedEventArgs e)
    {
        var success = await RunOperationAsync("移除 Harness 登录自启动", async cancellationToken =>
        {
            harnessStartupService ??= new DeepSeekHarnessStartupService(appPaths);
            await harnessStartupService.RemoveAsync(cancellationToken);
            HarnessStartupStatusText.Text = "未配置登录自启动；当前已运行的 Host 不受影响。";
        }, showProgress: false);
        if (success) MessageBox.Show("已移除登录自启动。当前 Host 不会被强制停止。", "设置完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Harness 任务中心刷新请求：单飞（进行中重复调用直接跳过，手动刷新排队绝不并发）；
    /// 自动刷新仅在协作开发页可见且 Harness 面板激活时发起；关闭 Helper 只停止刷新，不停止 Harness 任务。</summary>
    private void RequestHarnessTaskRefresh(bool manual)
    {
        if (taskRefreshCoordinator.Closed) return;
        if (!manual && (!taskRefreshCoordinator.PageVisible || SelectedCollaborationMode() != CollaborationMode.Harness)) return;
        if (harnessTaskRefreshInFlight)
        {
            if (manual) harnessTaskRefreshQueued = true;
            return;
        }
        harnessTaskRefreshInFlight = true;
        _ = RunHarnessTaskReadAsync(manual);
    }

    /// <summary>
    /// 后台读取一次 Harness 任务快照并回到 UI 线程渲染；窗口关闭后不回写 UI。
    /// 读取前先执行异步 recent-tasks 对账（一次 session.list 核对本地 running/starting 状态，
    /// 真实会话已结束时自动写回终态，不再显示假运行中）；Host 不可达时对账静默跳过、
    /// 列表仍按本地状态文件展示（离线兼容）。
    /// </summary>
    private async Task RunHarnessTaskReadAsync(bool manual)
    {
        IReadOnlyList<HarnessTaskStatus>? tasks = null;
        Exception? failure = null;
        try
        {
            tasks = await Task.Run(async () =>
            {
                var runner = GetHarnessRunner();
                try { await runner.ReconcileRecentTasksAsync(); }
                catch { /* 对账失败不阻断列表读取（Host 不可达/取消等） */ }
                return runner.GetRecentTasks(50)
                    .OrderByDescending(task => task.IsRunning)
                    .ThenByDescending(task => task.UpdatedUtc)
                    .ToList();
            });
        }
        catch (Exception ex) { failure = ex; }

        await Dispatcher.InvokeAsync(() =>
        {
            if (taskRefreshCoordinator.Closed) return;
            try
            {
                if (tasks is null)
                {
                    RenderHarnessTaskError(failure?.Message ?? "未知错误");
                }
                // 自动刷新仅在协作页可见且 Harness 面板激活时渲染；手动刷新不受页面可见限制。
                else if (manual || (taskRefreshCoordinator.PageVisible && SelectedCollaborationMode() == CollaborationMode.Harness))
                {
                    var fingerprint = HarnessTasksFingerprint(tasks);
                    if (!string.Equals(harnessTaskRenderedFingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        RenderHarnessTaskView(tasks);
                        harnessTaskRenderedFingerprint = fingerprint;
                    }
                }
            }
            finally
            {
                if (harnessTaskRefreshQueued)
                {
                    harnessTaskRefreshQueued = false;
                    _ = RunHarnessTaskReadAsync(manual: true);
                }
                else harnessTaskRefreshInFlight = false;
            }
        });
    }

    private DeepSeekHarnessRunner GetHarnessRunner() => harnessRunner ??= new DeepSeekHarnessRunner(appPaths);

    /// <summary>任务快照指纹：稳定顺序序列化后取 SHA-256；相同内容不触发 UI 重建。</summary>
    private static string HarnessTasksFingerprint(IReadOnlyList<HarnessTaskStatus> tasks)
    {
        var builder = new StringBuilder();
        foreach (var task in tasks.OrderBy(task => task.TaskId, StringComparer.Ordinal))
        {
            builder.Append(task.TaskId).Append('|').Append(task.State).Append('|').Append(task.Message)
                .Append('|').Append(task.UpdatedUtc.Ticks).Append('|').Append(task.SessionId).Append('\n');
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    /// <summary>把任务快照渲染到列表与详情；选择保持，无选择时自动选运行中最新 / 最新任务。</summary>
    private void RenderHarnessTaskView(IReadOnlyList<HarnessTaskStatus> tasks)
    {
        HarnessTaskListBox.Items.Clear();
        HarnessTaskListHintText.Text = tasks.Count == 0 ? "暂无任务" : "暂无可读取的任务。";
        HarnessTaskListHintText.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        suppressHarnessSelection = true;
        try
        {
            foreach (var task in tasks) HarnessTaskListBox.Items.Add(BuildHarnessTaskRow(task));
            ListBoxItem? target = null;
            if (selectedHarnessTaskId is not null) target = FindHarnessTaskListItem(selectedHarnessTaskId);
            target ??= (tasks.FirstOrDefault(task => task.IsRunning) ?? tasks.FirstOrDefault()) is { } auto
                ? FindHarnessTaskListItem(auto.TaskId)
                : null;
            if (target is not null) HarnessTaskListBox.SelectedItem = target;
        }
        finally { suppressHarnessSelection = false; }

        selectedHarnessTask = (HarnessTaskListBox.SelectedItem as ListBoxItem)?.Tag as HarnessTaskStatus;
        selectedHarnessTaskId = selectedHarnessTask?.TaskId;
        UpdateHarnessTaskDetail();
        UpdateHarnessTaskActionButtons();
    }

    private void RenderHarnessTaskError(string message)
    {
        selectedHarnessTask = null;
        selectedHarnessTaskId = null;
        HarnessTaskListBox.Items.Clear();
        HarnessTaskListHintText.Text = "Harness 任务状态读取失败：" + message;
        HarnessTaskListHintText.Visibility = Visibility.Visible;
        HarnessTaskDetailText.Visibility = Visibility.Collapsed;
        UpdateHarnessTaskActionButtons();
    }

    /// <summary>列表行：状态标记 + 状态文案 + 任务 ID；元信息行：已运行/耗时 · 会话 ID（存在时）。</summary>
    private ListBoxItem BuildHarnessTaskRow(HarnessTaskStatus task)
    {
        var colorKey = HarnessTaskColorKey(task);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 3, 2, 1) };
        titleRow.Children.Add(new TextBlock { Text = "●", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Foreground = StatusBrush(colorKey) });
        titleRow.Children.Add(new TextBlock { Text = HarnessTaskStatusText(task), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = StatusBrush(colorKey) });
        titleRow.Children.Add(new TextBlock { Text = "  " + task.TaskId, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        var sessionPart = string.IsNullOrWhiteSpace(task.SessionId) ? "会话：未记录" : "会话：" + task.SessionId;
        var meta = new TextBlock
        {
            Text = $"耗时 {FormatHarnessDuration(HarnessElapsed(task))} · {sessionPart}",
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
            Margin = new Thickness(2, 0, 2, 1)
        };

        var panel = new StackPanel();
        panel.Children.Add(titleRow);
        panel.Children.Add(meta);
        return new ListBoxItem { Content = panel, Tag = task };
    }

    private static string HarnessTaskStatusText(HarnessTaskStatus task) => task.State.ToLowerInvariant() switch
    {
        "running" => "运行中",
        "starting" => "启动中",
        "awaiting-gpt" => "等待 GPT 验收",
        "completed" => "已完成",
        "failed" => "失败",
        "cancelled" => "已停止",
        _ => task.State
    };

    private static string HarnessTaskColorKey(HarnessTaskStatus task) => task.State.ToLowerInvariant() switch
    {
        "running" or "starting" => "running",
        "awaiting-gpt" or "completed" => "completed",
        "failed" => "failed",
        "cancelled" => "pending",
        _ => "pending"
    };

    /// <summary>运行中任务显示已运行时长；终态显示实际耗时。</summary>
    private static TimeSpan HarnessElapsed(HarnessTaskStatus task)
        => task.IsRunning ? DateTime.UtcNow - task.StartedUtc : task.UpdatedUtc - task.StartedUtc;

    private static string FormatHarnessDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) return "—";
        if (duration.TotalHours >= 1) return $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分";
        if (duration.TotalMinutes >= 1) return $"{duration.Minutes} 分 {duration.Seconds} 秒";
        return $"{Math.Max(0, duration.Seconds)} 秒";
    }

    private ListBoxItem? FindHarnessTaskListItem(string taskId)
    {
        foreach (var item in HarnessTaskListBox.Items)
            if (item is ListBoxItem { Tag: HarnessTaskStatus task } && string.Equals(task.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
                return (ListBoxItem)item;
        return null;
    }

    private void HarnessTaskListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressHarnessSelection) return;
        selectedHarnessTask = (HarnessTaskListBox.SelectedItem as ListBoxItem)?.Tag as HarnessTaskStatus;
        selectedHarnessTaskId = selectedHarnessTask?.TaskId;
        UpdateHarnessTaskDetail();
        UpdateHarnessTaskActionButtons();
    }

    /// <summary>选中任务详情：状态、会话状态、任务 ID、会话 ID、执行模式/权限/强度、耗时、当前消息、Web URL、任务目录。
    /// 不显示虚假预计时间：只显示已运行耗时与真实会话状态。</summary>
    private void UpdateHarnessTaskDetail()
    {
        var task = selectedHarnessTask;
        if (task is null)
        {
            HarnessTaskDetailText.Visibility = Visibility.Collapsed;
            return;
        }
        var sessionState = string.IsNullOrWhiteSpace(task.SessionState) ? "unknown" : task.SessionState switch
        {
            "creating" => "创建中",
            "resuming" => "接回中",
            "degraded-standard" => "已降级 standard",
            "running" => "运行中",
            "completed" => "已完成",
            "cancelled" => "已停止",
            "failed" => "失败",
            _ => task.SessionState
        };
        var lines = new List<string>
        {
            $"执行器：{task.Executor}",
            $"状态：{HarnessTaskStatusText(task)}",
            $"任务 ID：{task.TaskId}",
            $"会话 ID：{(string.IsNullOrWhiteSpace(task.SessionId) ? "未记录" : task.SessionId)}",
            $"会话状态：{sessionState}",
            $"执行模式：{task.ExecutionMode}（agentPreset 由模式映射，预设未确认时降级 standard）",
            $"权限：{task.PermissionMode}（经 Helper 托管 Host 受控环境传递，不进入命令行/日志）",
            $"执行强度：{task.ExecutionStrength}（Helper 的检查预算与收敛策略，非模型思考强度）",
            $"耗时：{FormatHarnessDuration(HarnessElapsed(task))}（开始 {task.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}）",
            $"当前消息：{task.Message}",
            $"Web：{task.WebUrl}"
        };
        // awaiting-gpt 不是最终完成：给出 GPT 独立验收的下一步说明，避免误认为产品已交付。
        if (string.Equals(task.State, "awaiting-gpt", StringComparison.OrdinalIgnoreCase))
            lines.Add("下一步：等待 GPT 独立验收——检查实际 diff 并执行 ACCEPTANCE.md 聚焦验收；验收通过前不算产品交付完成。");
        lines.Add($"实际指标：{task.Steps} 步；新增输入 {task.UncachedInputTokens:N0}；缓存输入 {task.CacheReadTokens:N0}；输出 {task.OutputTokens:N0}");
        // 推理流摘要：reasoning 事件只作为诊断计数展示，绝不计入 steps，也不与缓存 token 混为进度。
        lines.Add($"推理流摘要：阶段 {StageText(task.Stage)}；工具调用 {task.ToolCallCount:N0}；推理流事件 {task.ReasoningEventCount:N0}（不计为步骤）；无进展告警 {task.NoProgressWarnings}");
        lines.Add("模型：" + (string.IsNullOrWhiteSpace(task.Model) ? HarnessExecutionOptions.ModelReasoningText : task.Model));
        lines.Add($"合同：{task.ExecutionMode} · 权限：{task.PermissionMode} · 强度：{task.ExecutionStrength}");
        lines.Add("会话状态：" + task.SessionState);
        if (!string.IsNullOrWhiteSpace(task.RootCauseKey)) lines.Add($"合同组键（rootCauseKey）：{task.RootCauseKey}（同项目同组键的运行中任务会被接回，而非新建会话）");
        if (!string.IsNullOrWhiteSpace(task.TaskDirectory)) lines.Add($"任务目录：{task.TaskDirectory}");
        lines.Add("状态来源：" + HarnessStateSourceText(task.StateSource));
        HarnessTaskDetailText.Text = string.Join(Environment.NewLine, lines);
        HarnessTaskDetailText.Visibility = Visibility.Visible;
    }

    /// <summary>任务状态来源 → 中文展示：真实状态以任务目录真相源为准，旧注册表只作兼容读取并自动迁移。</summary>
    private static string HarnessStateSourceText(string? source) => source switch
    {
        "task-directory" => "任务目录真相源（HARNESS_STATUS.json）",
        "legacy-registry" => "旧注册表索引（兼容读取；新运行会自动迁移到任务目录），如曾降级会显示在消息中",
        _ => "未知"
    };

    /// <summary>任务阶段 → 中文展示（用于推理流摘要；未知阶段显示"未知"）。</summary>
    private static string StageText(string? stage) => stage?.ToLowerInvariant() switch
    {
        "start" => "阶段开始",
        "plan" => "计划",
        "read" => "读取",
        "edit" => "编辑",
        "check" => "检查",
        "report" => "报告/结束",
        _ => string.IsNullOrWhiteSpace(stage) ? "未知" : stage
    };

    private void UpdateHarnessTaskActionButtons()
    {
        var task = selectedHarnessTask;
        StopHarnessTaskButton.IsEnabled = task?.IsRunning == true;
        CopyHarnessTaskIdButton.IsEnabled = task is not null;
        CopyHarnessSessionIdButton.IsEnabled = task is not null && !string.IsNullOrWhiteSpace(task.SessionId);
        OpenHarnessTaskDirectoryButton.IsEnabled = task is not null && !string.IsNullOrWhiteSpace(task.TaskDirectory);
        OpenHarnessTaskInWebButton.IsEnabled = task is not null && !string.IsNullOrWhiteSpace(task.SessionId);
    }

    private void RefreshHarnessTasks_Click(object sender, RoutedEventArgs e) => RequestHarnessTaskRefresh(manual: true);

    private async void StopHarnessTask_Click(object sender, RoutedEventArgs e)
    {
        var task = selectedHarnessTask;
        if (task is null || !task.IsRunning) return;
        if (MessageBox.Show("将向 Harness Web Host 发送 session.cancel 请求，停止所选任务的会话。已写入的项目文件不会删除。继续吗？", "停止 Harness 任务", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var runner = GetHarnessRunner();
        var success = await RunOperationAsync("停止 Harness 任务", async cancellationToken =>
        {
            var result = await runner.StopTaskAsync(task.TaskId);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!taskRefreshCoordinator.Closed)
                    MessageBox.Show(result.Message, result.Requested ? "已发送取消请求" : "无法取消", MessageBoxButton.OK, result.Requested ? MessageBoxImage.Information : MessageBoxImage.Warning);
            });
        }, showProgress: false);
        if (success) RequestHarnessTaskRefresh(manual: true);
    }

    private void CopyHarnessTaskId_Click(object sender, RoutedEventArgs e)
    {
        if (selectedHarnessTask is null) return;
        try
        {
            Clipboard.SetText(selectedHarnessTask.TaskId);
            MessageBox.Show($"已复制任务 ID：{selectedHarnessTask.TaskId}", "已复制", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void CopyHarnessSessionId_Click(object sender, RoutedEventArgs e)
    {
        var sessionId = selectedHarnessTask?.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        try
        {
            Clipboard.SetText(sessionId);
            MessageBox.Show($"已复制会话 ID：{sessionId}", "已复制", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenHarnessTaskDirectory_Click(object sender, RoutedEventArgs e)
    {
        var directory = selectedHarnessTask?.TaskDirectory;
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            if (Directory.Exists(directory))
                Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
            else
                MessageBox.Show("任务目录不存在：" + directory, "无法打开", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    /// <summary>会话 ID 存在时在 Harness Web 中打开同一任务。rc.6 Web 是无深链的 SPA，
    /// 打开 Web 根地址后会话在侧边栏中可见；不点击/自动化 Web UI。</summary>
    private void OpenHarnessTaskInWeb_Click(object sender, RoutedEventArgs e)
    {
        var task = selectedHarnessTask;
        if (task is null || string.IsNullOrWhiteSpace(task.SessionId)) return;
        var url = string.IsNullOrWhiteSpace(task.WebUrl) ? DeepSeekHarnessVersions.WebHostDefaultUrl : task.WebUrl;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void UpdateReasonixEnvironment(ReasonixCliSelection selection, ReasonixStatus status)
    {
        if (ReasonixEnvironmentText is null) return;
        var best = selection.Best;
        if (best is null)
        {
            ReasonixEnvironmentText.Text = "未找到 Reasonix CLI。" + (selection.DiscoveryNote is null ? string.Empty : " " + selection.DiscoveryNote);
            ReasonixEnvironmentText.ToolTip = null;
            return;
        }
        var compatibility = status.ProtocolCompatibility switch
        {
            "compatible" => "协议兼容",
            "legacy" => "协议不兼容（旧版）",
            _ => "协议未知"
        };
        var line = $"CLI：{best.Path} · 版本 {(string.IsNullOrWhiteSpace(best.Version) ? "未知" : best.Version)} · 来源 {ReasonixCliProbe.DescribeSource(best.Source)} · {compatibility}";
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(status.DoctorWarning)) notes.Add(status.DoctorWarning);
        if (!string.IsNullOrWhiteSpace(status.DiscoveryNote)) notes.Add(status.DiscoveryNote);
        ReasonixEnvironmentText.Text = notes.Count == 0 ? line : line + Environment.NewLine + string.Join(Environment.NewLine, notes);
        ReasonixEnvironmentText.ToolTip = ReasonixEnvironmentText.Text;
    }

    private async void ReasonixRescan_Click(object sender, RoutedEventArgs e)
    {
        ReasonixCliSelection? selection = null;
        var success = await RunOperationAsync("重新扫描 Reasonix CLI", async cancellationToken =>
        {
            // 探测在 DiscoverBestAsync 内完成（含超时）；结果直接复用于一次刷新，不重复全套探测。
            selection = await new ReasonixIntegrationService(settings.CodexRoot, appPaths).DiscoverBestAsync(cancellationToken);
        }, showProgress: false);
        if (success) await RefreshSubagentSettingsAsync(selection);
    }

    private async void ReasonixSelectCli_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Reasonix CLI",
            Filter = "Reasonix CLI (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return; // 取消：不改状态
        ReasonixCliSelection? selection = null;
        var success = await RunOperationAsync("选择 Reasonix CLI", async cancellationToken =>
        {
            var service = new ReasonixIntegrationService(settings.CodexRoot, appPaths);
            selection = await service.SelectCliAsync(dialog.FileName, cancellationToken);
            var status = await service.DiagnoseAsync(precomputedSelection: selection, cancellationToken: cancellationToken);
            await Dispatcher.InvokeAsync(() => UpdateReasonixEnvironment(selection, status));
        }, showProgress: false);
        if (success) await RefreshSubagentSettingsAsync(selection);
    }

    private async void SubagentSettingsActionButton_Click(object sender, RoutedEventArgs e)
    {
        var service = new ReasonixIntegrationService(settings.CodexRoot, appPaths);
        var status = await service.DiagnoseAsync();
        var enabled = status.IntegrationEnabled;
        var action = enabled ? "关闭协作编码" : "开启协作编码";
        var explanation = enabled
            ? "将删除 Helper 管理的 Reasonix Skill 和协作规则，GPT 恢复独立开发。不会删除 Reasonix、模型凭据、项目或其他 Skills。"
            : "将安装 Helper 管理的 Reasonix 执行 Skill。GPT 负责规划和验收，实现类任务按规模三档路由（微任务 GPT 直接实现、Reasonix 单合同、Reasonix 有限并行）；Codex 原生子智能体保持关闭。";
        if (MessageBox.Show(explanation + "\n\n需要先安全退出 Codex，继续吗？", action, MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        if (!await EnsureCodexStoppedAsync()) return;
        var permissionMode = SelectedReasonixPermissionMode();
        var success = await RunOperationAsync(action, cancellationToken => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enabled) service.Disable();
            else service.Enable(status.ExecutablePath, status.DefaultModel, permissionMode);
        }, cancellationToken), showProgress: false);
        await RefreshSubagentSettingsAsync();
        if (success) MessageBox.Show(enabled ? "已关闭。GPT 将独立完成任务。" : "已开启。重新打开 Codex 后，新任务将由 GPT 规划、Reasonix 编码、GPT 验收。", "设置完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void TestReasonixConnection_Click(object sender, RoutedEventArgs e)
    {
        var success = await RunOperationAsync("测试 Reasonix 连接", async cancellationToken =>
        {
            var result = await new ReasonixIntegrationService(settings.CodexRoot, appPaths).TestConnectionAsync(cancellationToken);
            await Dispatcher.InvokeAsync(() => MessageBox.Show(result, "Reasonix 连接正常", MessageBoxButton.OK, MessageBoxImage.Information));
        }, showProgress: false);
        await RefreshSubagentSettingsAsync();
    }

    private async void ApplyReasonixDefaultModel_Click(object sender, RoutedEventArgs e)
    {
        if (ReasonixDefaultModelBox.SelectedItem is not ReasonixModelOption selected) return;
        var success = await RunOperationAsync("设置 Reasonix 默认模型", async cancellationToken =>
        {
            var service = new ReasonixIntegrationService(settings.CodexRoot, appPaths);
            await service.SetDefaultModelAsync(selected.Id, cancellationToken);
            var result = await service.TestConnectionAsync(cancellationToken);
            await Dispatcher.InvokeAsync(() => MessageBox.Show($"默认模型已切换为 {selected.Id}，并通过最小连接测试。\n\n{result}", "模型已应用", MessageBoxButton.OK, MessageBoxImage.Information));
        }, showProgress: false);
        if (success) await RefreshSubagentSettingsAsync();
    }

    private ReasonixPermissionMode SelectedReasonixPermissionMode() =>
        Enum.TryParse<ReasonixPermissionMode>((ReasonixPermissionModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var mode) ? mode : ReasonixPermissionMode.Full;

    private void SelectReasonixPermissionMode(ReasonixPermissionMode mode)
    {
        foreach (var item in ReasonixPermissionModeBox.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase)) { ReasonixPermissionModeBox.SelectedItem = item; return; }
    }

    private async void ApplyReasonixPermissionMode_Click(object sender, RoutedEventArgs e)
    {
        var mode = SelectedReasonixPermissionMode();
        if (mode == ReasonixPermissionMode.Full && MessageBox.Show("完全权限会让 Reasonix 跳过全部工具审批，并可执行任意命令。任务合同、项目锁和日志仍保留。确认启用吗？", "启用完全权限", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var success = await RunOperationAsync("应用 Reasonix 权限", async cancellationToken =>
        {
            await Task.Run(() => new ReasonixIntegrationService(settings.CodexRoot, appPaths).SetPermissionMode(mode), cancellationToken);
        }, showProgress: false);
        if (success)
        {
            MessageBox.Show(mode == ReasonixPermissionMode.Full ? "已启用完全权限。之后提交的 Reasonix 任务会跳过工具审批。" : "已切换为安全开发模式。", "权限已保存", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshSubagentSettingsAsync();
        }
    }

    private void RefreshReasonixTasks_Click(object sender, RoutedEventArgs e) => RequestReasonixTaskRefresh(manual: true);

    /// <summary>
    /// 请求一次 Reasonix 任务刷新。自动刷新由协调器门控（仅协作页可见、单飞）；手动刷新立即生效且保持选择。
    /// 磁盘读取、JSON 解析、进程探测与自动恢复全部在后台线程完成，UI 线程只回传渲染。
    /// </summary>
    private void RequestReasonixTaskRefresh(bool manual)
    {
        var started = manual ? taskRefreshCoordinator.TryStartManualRead() : taskRefreshCoordinator.TryStartAutoRead();
        if (started) _ = RunReasonixTaskReadAsync(manual);
    }

    /// <summary>后台读取一次 Reasonix 任务快照并回到 UI 线程按协调器判定渲染（不重建无变化内容）。</summary>
    private async Task RunReasonixTaskReadAsync(bool manual)
    {
        if (taskRefreshCoordinator.Closed) return;
        ReasonixTaskListView? view = null;
        Exception? failure = null;
        try { view = await BuildReasonixTaskListViewAsync(); }
        catch (Exception ex) { failure = ex; }

        await Dispatcher.InvokeAsync(() =>
        {
            // 关闭窗口后不得继续写入已关闭窗口。
            if (taskRefreshCoordinator.Closed) return;
            try
            {
                if (view is null)
                {
                    RenderReasonixTaskError(failure?.Message ?? "未知错误");
                }
                // 自动刷新仅在协作页可见时渲染；手动刷新（用户/操作主动）不受页面可见限制，始终渲染。
                else if (manual || taskRefreshCoordinator.PageVisible)
                {
                    // 无变化快照不得清空重建控件；有变化（或首次）才重建并记录指纹。
                    if (taskRefreshCoordinator.HasContentChanged(view.Snapshot, view.WorkerChecks))
                    {
                        RenderReasonixTaskView(view);
                        taskRefreshCoordinator.RecordRendered(view.Snapshot, view.WorkerChecks);
                    }
                }
            }
            finally
            {
                // 排队的手动刷新在当前读取完成后自动再执行一次（绝不并发）。
                if (taskRefreshCoordinator.CompleteRead())
                    _ = RunReasonixTaskReadAsync(manual: true);
            }
        });
    }

    /// <summary>在后台线程构建只读任务视图：读取快照、调度统计、选中任务与 workerChecks 步骤。</summary>
    private Task<ReasonixTaskListView> BuildReasonixTaskListViewAsync()
        => Task.Run(() =>
        {
            var service = new ReasonixIntegrationService(settings.CodexRoot, appPaths);
            var snapshot = service.GetRecentTasks(50);
            // 运行中优先，同一优先级内按更新时间倒序，保证“运行中多项同时可见”而不仅是 latest。
            var tasks = snapshot.Tasks
                .OrderByDescending(task => task.IsRunning)
                .ThenByDescending(task => task.UpdatedUtc)
                .ToList();

            // 复用 ReasonixParallelScheduler：以各任务当前状态为输入，生成顶部统计与每行的等待/冲突/排队原因。
            var schedule = new ReasonixParallelScheduler().Schedule(tasks.Select(ToSchedulerTask).ToList());
            var decisions = schedule.Decisions.ToDictionary(decision => decision.TaskId, StringComparer.OrdinalIgnoreCase);

            // 选择保持：有选中保留，否则自动选运行中最新 / 最新任务；均在后台确定，避免 UI 线程重复读取。
            var currentSelection = selectedReasonixTaskId;
            var selectedId = currentSelection is not null && tasks.Any(task => string.Equals(task.TaskId, currentSelection, StringComparison.OrdinalIgnoreCase))
                ? currentSelection
                : (tasks.FirstOrDefault(task => task.IsRunning) ?? tasks.FirstOrDefault())?.TaskId;

            IReadOnlyList<string>? workerChecks = null;
            IReadOnlyList<ReasonixWorkerStep>? steps = null;
            string? retryBlockReason = null;
            if (selectedId is not null)
            {
                var task = tasks.First(item => string.Equals(item.TaskId, selectedId, StringComparison.OrdinalIgnoreCase));
                workerChecks = service.ReadWorkerChecks(task);
                steps = ReasonixUiText.BuildWorkerSteps(task, workerChecks);
                retryBlockReason = service.RetryBlockReason(task);
            }

            return new ReasonixTaskListView(snapshot, tasks, snapshot.Diagnostics, schedule.Snapshot, decisions, selectedId, steps, workerChecks ?? [], retryBlockReason);
        });

    /// <summary>把新的只读任务视图渲染到控件（仅在内容变化后调用）。</summary>
    private void RenderReasonixTaskView(ReasonixTaskListView view)
    {
        RenderReasonixStats(view.Schedule);
        RenderReasonixDiagnostics(view.Diagnostics);

        ReasonixTaskListBox.Items.Clear();
        ReasonixTaskListHintText.Text = view.Tasks.Count == 0 && view.Diagnostics.Count == 0
            ? "暂无任务"
            : "暂无可读取的任务，详见上方诊断。";
        ReasonixTaskListHintText.Visibility = view.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        suppressReasonixSelection = true;
        try
        {
            foreach (var task in view.Tasks)
            {
                view.Decisions.TryGetValue(task.TaskId, out var decision);
                ReasonixTaskListBox.Items.Add(BuildTaskRow(task, decision));
            }
            // 保留选择；没有选择时自动选运行中最新任务，否则选最新任务。
            ListBoxItem? target = null;
            if (view.SelectedTaskId is not null) target = FindTaskListItem(view.SelectedTaskId);
            target ??= (view.Tasks.FirstOrDefault(task => task.IsRunning) ?? view.Tasks.FirstOrDefault()) is { } auto
                ? FindTaskListItem(auto.TaskId)
                : null;
            if (target is not null) ReasonixTaskListBox.SelectedItem = target;
        }
        finally { suppressReasonixSelection = false; }

        selectedReasonixTask = (ReasonixTaskListBox.SelectedItem as ListBoxItem)?.Tag as ReasonixTaskStatus;
        selectedReasonixTaskId = selectedReasonixTask?.TaskId;
        retryBlockReasonForSelected = view.RetryBlockReason;
        UpdateReasonixTaskActionButtons();
        RenderReasonixSteps(view.Steps);
    }

    private void RenderReasonixTaskError(string message)
    {
        selectedReasonixTask = null;
        ReasonixTaskListBox.Items.Clear();
        ReasonixStatsPanel.Children.Clear();
        ReasonixTaskListHintText.Text = "Reasonix 任务状态读取失败：" + message;
        ReasonixTaskListHintText.Visibility = Visibility.Visible;
        retryBlockReasonForSelected = null;
        UpdateReasonixTaskActionButtons();
        RenderReasonixSteps(null);
    }

    /// <summary>把任务状态映射为调度器输入（复用 ReasonixParallelScheduler 的领域状态枚举）。</summary>
    private static ReasonixSchedulerTask ToSchedulerTask(ReasonixTaskStatus task)
    {
        var state = task.IsRunning ? ReasonixTaskState.Running
            : string.Equals(task.State, "completed", StringComparison.OrdinalIgnoreCase) ? ReasonixTaskState.Completed
            : task.IsRetryableState ? ReasonixTaskState.Failed
            : ReasonixTaskState.Queued;
        return new ReasonixSchedulerTask(task.TaskId, task.TaskId, task.TaskDirectory, task.ProjectRoot, Array.Empty<string>(), Array.Empty<string>(), state);
    }

    /// <summary>顶部统计行：运行中 / 排队 / 受阻 / 已完成 / 失败 / 最大并发。颜色区分但附带文字，不只靠颜色。</summary>
    private void RenderReasonixStats(ReasonixSchedulerSnapshot snapshot)
    {
        ReasonixStatsPanel.Children.Clear();
        ReasonixStatsPanel.Children.Add(BuildStatChip($"运行中 {snapshot.Running}", "running"));
        ReasonixStatsPanel.Children.Add(BuildStatChip($"排队 {snapshot.Queued}", "pending"));
        ReasonixStatsPanel.Children.Add(BuildStatChip($"受阻 {snapshot.Blocked}", "pending"));
        ReasonixStatsPanel.Children.Add(BuildStatChip($"已完成 {snapshot.Completed}", "completed"));
        ReasonixStatsPanel.Children.Add(BuildStatChip($"失败 {snapshot.Failed}", "failed"));
        ReasonixStatsPanel.Children.Add(BuildStatChip($"最大并发 {snapshot.MaxConcurrency}", "other"));
    }

    private Border BuildStatChip(string text, string colorKey)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = StatusBrush(colorKey),
            Margin = new Thickness(8, 3, 8, 3)
        };
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0x00, 0x00, 0x00)),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 6, 6),
            Child = label
        };
    }

    /// <summary>损坏状态文件诊断：显示摘要但绝不让页面崩溃。</summary>
    private void RenderReasonixDiagnostics(IReadOnlyList<ReasonixTaskDiagnostic> diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            ReasonixTasksDiagnosticText.Visibility = Visibility.Collapsed;
            return;
        }
        var names = string.Join("、", diagnostics.Take(3).Select(diagnostic => diagnostic.FileName));
        if (diagnostics.Count > 3) names += " 等";
        ReasonixTasksDiagnosticText.Text = $"⚠ {diagnostics.Count} 个状态文件无法读取：{names}（{diagnostics.First().Reason}）";
        ReasonixTasksDiagnosticText.Visibility = Visibility.Visible;
    }

    /// <summary>构建任务列表中的一行：状态标记 + ID + 阶段 / 模型·时间·剩余·检查 / 等待冲突原因与当前检查。</summary>
    private ListBoxItem BuildTaskRow(ReasonixTaskStatus task, ReasonixTaskDecision? decision)
    {
        var colorKey = ReasonixUiText.StateColorKey(task);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 3, 2, 1) };
        titleRow.Children.Add(new TextBlock { Text = "●", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Foreground = StatusBrush(colorKey) });
        titleRow.Children.Add(new TextBlock { Text = TaskStatusText(task), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = StatusBrush(colorKey) });
        if (!string.IsNullOrWhiteSpace(task.Phase))
            titleRow.Children.Add(new TextBlock { Text = " · " + task.Phase, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = StatusBrush(colorKey) });
        titleRow.Children.Add(new TextBlock { Text = "  " + task.TaskId, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        var meta = new TextBlock
        {
            Text = BuildTaskMetaLine(task),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
            Margin = new Thickness(2, 0, 2, 1)
        };

        var reason = BuildTaskReasonLine(task, decision);
        var panel = new StackPanel();
        panel.Children.Add(titleRow);
        panel.Children.Add(meta);
        if (!string.IsNullOrWhiteSpace(reason))
            panel.Children.Add(new TextBlock { Text = reason, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("SecondaryTextBrush"), Margin = new Thickness(2, 0, 2, 3) });

        return new ListBoxItem { Content = panel, Tag = task };
    }

    private static string TaskStatusText(ReasonixTaskStatus task) => task.State.ToLowerInvariant() switch
    {
        "running" => "运行中",
        "starting" => "启动中",
        "completed" => "已完成",
        "failed" => "失败",
        "cancelled" => "已停止",
        "interrupted" => "中断",
        _ => task.State
    };

    /// <summary>元信息行：执行模型 · 时间线（已运行/预计剩余）· 完成检查数。</summary>
    private static string BuildTaskMetaLine(ReasonixTaskStatus task)
    {
        var model = string.IsNullOrWhiteSpace(task.ExecutionModel)
            ? (string.IsNullOrWhiteSpace(task.ExecutionProfile) ? "未记录" : task.ExecutionProfile)
            : task.ExecutionModel;
        var checks = task.TotalChecks is > 0 ? $"{task.CompletedChecks ?? 0}/{task.TotalChecks} 项检查" : "检查数未记录";
        return $"模型 {model} · {ReasonixUiText.TimeLine(task)} · {checks}";
    }

    /// <summary>等待/冲突/排队原因优先展示；运行中显示当前检查。</summary>
    private static string BuildTaskReasonLine(ReasonixTaskStatus task, ReasonixTaskDecision? decision)
    {
        var parts = new List<string>();
        if (decision is not null && decision.Status is ReasonixDecisionStatus.WaitingDependency or ReasonixDecisionStatus.WaitingConflict or ReasonixDecisionStatus.WaitingMerge or ReasonixDecisionStatus.Queued
            && !string.IsNullOrWhiteSpace(decision.Reason))
            parts.Add(decision.Reason);
        if (!string.IsNullOrWhiteSpace(task.CurrentCheck)) parts.Add("当前检查：" + task.CurrentCheck);
        return string.Join(" · ", parts);
    }

    private ListBoxItem? FindTaskListItem(string taskId)
    {
        foreach (var item in ReasonixTaskListBox.Items)
            if (item is ListBoxItem { Tag: ReasonixTaskStatus task } && string.Equals(task.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
                return (ListBoxItem)item;
        return null;
    }

    private void ReasonixTaskListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressReasonixSelection) return;
        selectedReasonixTask = (ReasonixTaskListBox.SelectedItem as ListBoxItem)?.Tag as ReasonixTaskStatus;
        selectedReasonixTaskId = selectedReasonixTask?.TaskId;
        UpdateReasonixTaskActionButtons();
        // 选中任务的 workerChecks 与重试阻断在后台读取，绝不在 UI 线程做磁盘读取。
        _ = RefreshReasonixStepsForSelectionAsync();
    }

    /// <summary>在后台读取选中任务的 workerChecks 步骤与重试阻断原因并回传渲染。</summary>
    private async Task RefreshReasonixStepsForSelectionAsync()
    {
        var task = selectedReasonixTask;
        if (task is null)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (taskRefreshCoordinator.Closed || selectedReasonixTask is not null) return;
                retryBlockReasonForSelected = null;
                RenderReasonixSteps(null);
                UpdateReasonixTaskActionButtons();
            });
            return;
        }
        var service = new ReasonixIntegrationService(settings.CodexRoot, appPaths);
        var loaded = await Task.Run(() =>
        {
            var checks = service.ReadWorkerChecks(task);
            return (Steps: ReasonixUiText.BuildWorkerSteps(task, checks), RetryBlock: service.RetryBlockReason(task));
        });
        await Dispatcher.InvokeAsync(() =>
        {
            if (taskRefreshCoordinator.Closed) return;
            if (!string.Equals(selectedReasonixTaskId, task.TaskId, StringComparison.OrdinalIgnoreCase)) return;
            retryBlockReasonForSelected = loaded.RetryBlock;
            UpdateReasonixTaskActionButtons();
            RenderReasonixSteps(loaded.Steps);
        });
    }

    private void UpdateReasonixTaskActionButtons()
    {
        var task = selectedReasonixTask;
        if (task is null)
        {
            StopReasonixTaskButton.IsEnabled = false;
            RetryReasonixTaskButton.IsEnabled = false;
            ReturnToCodexTaskButton.IsEnabled = false;
            CopyReasonixTaskIdButton.IsEnabled = false;
            OpenReasonixTaskDirectoryButton.IsEnabled = false;
            return;
        }
        StopReasonixTaskButton.IsEnabled = task.IsRunning;
        RetryReasonixTaskButton.IsEnabled = retryBlockReasonForSelected is null;
        ReturnToCodexTaskButton.IsEnabled = CodexThreadUri.Build(task.ReturnUri, task.CodexThreadId) is not null;
        CopyReasonixTaskIdButton.IsEnabled = true;
        OpenReasonixTaskDirectoryButton.IsEnabled = !string.IsNullOrWhiteSpace(task.TaskDirectory);
    }

    /// <summary>在“最近 Reasonix 任务”卡片摘要下逐项渲染 workerChecks 步骤（完成绿/当前蓝/待执行灰/失败红）。</summary>
    private void RenderReasonixSteps(IReadOnlyList<ReasonixWorkerStep>? steps)
    {
        ReasonixStepsPanel.Children.Clear();
        if (steps is null || steps.Count == 0) { ReasonixStepsPanel.Visibility = Visibility.Collapsed; return; }
        ReasonixStepsPanel.Visibility = Visibility.Visible;
        foreach (var step in steps)
        {
            var marker = new TextBlock
            {
                Text = "•",
                Width = 18,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = StatusBrush(step.State)
            };
            var text = new TextBlock
            {
                Text = step.Check,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = StatusBrush(step.State)
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(marker);
            row.Children.Add(text);
            ReasonixStepsPanel.Children.Add(row);
        }
    }

    private void ApplyReasonixIntensity_Click(object sender, RoutedEventArgs e)
    {
        var intensity = SelectedReasonixIntensity();
        settings.ReasonixExecutionIntensity = intensity;
        settingsService.Save(settings);
        MessageBox.Show($"已保存默认执行强度 {intensity}。manifest.json 显式声明优先；未声明时按此默认值并结合合同范围推断。新任务在 Codex Helper 中会显示实际采用的策略。", "执行强度已保存", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private string SelectedReasonixIntensity() => (ReasonixIntensityBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";

    private void SelectReasonixIntensity(string intensity)
    {
        foreach (var item in ReasonixIntensityBox.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag?.ToString(), intensity, StringComparison.OrdinalIgnoreCase)) { ReasonixIntensityBox.SelectedItem = item; return; }
    }

    /// <summary>并行协作设置：从 AppSettings 载入（旧配置缺字段时控件用默认值）。</summary>
    private void LoadParallelSettings(AppSettings settings)
    {
        AutoSplitCheck.IsChecked = settings.AutoSplitEnabled;
        ParallelIndependentCheck.IsChecked = settings.ParallelIndependentEnabled;
        AutoWorktreeCheck.IsChecked = settings.AutoWorktreeEnabled;
        ConvergeOnBudgetOverrunCheck.IsChecked = settings.ConvergeOnBudgetOverrunEnabled;
        SelectMaxConcurrency(settings.MaxConcurrency);
    }

    private void ApplyParallelSettings_Click(object sender, RoutedEventArgs e)
    {
        settings.AutoSplitEnabled = AutoSplitCheck.IsChecked ?? true;
        settings.ParallelIndependentEnabled = ParallelIndependentCheck.IsChecked ?? true;
        settings.AutoWorktreeEnabled = AutoWorktreeCheck.IsChecked ?? true;
        settings.ConvergeOnBudgetOverrunEnabled = ConvergeOnBudgetOverrunCheck.IsChecked ?? true;
        settings.MaxConcurrency = SelectedMaxConcurrency();
        settingsService.Save(settings);
        MessageBox.Show($"并行设置已保存：智能拆分 {(settings.AutoSplitEnabled ? "开" : "关")}、独立任务并行 {(settings.ParallelIndependentEnabled ? "开" : "关")}、最大并发 {settings.MaxConcurrency}、自动 worktree {(settings.AutoWorktreeEnabled ? "开" : "关")}、超预算收敛 {(settings.ConvergeOnBudgetOverrunEnabled ? "开" : "关")}。旧配置缺失的字段保留默认值。", "并行设置已保存", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private int SelectedMaxConcurrency()
    {
        if (int.TryParse((MaxConcurrencyBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var value))
            return Math.Clamp(value, ReasonixParallelScheduler.MinMaxConcurrency, ReasonixParallelScheduler.MaxMaxConcurrency);
        return ReasonixParallelScheduler.DefaultMaxConcurrency;
    }

    private void SelectMaxConcurrency(int value)
    {
        foreach (var item in MaxConcurrencyBox.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag?.ToString(), value.ToString(), StringComparison.Ordinal)) { MaxConcurrencyBox.SelectedItem = item; return; }
    }

    private void StopReasonixTask_Click(object sender, RoutedEventArgs e)
    {
        if (selectedReasonixTask is null || !selectedReasonixTask.IsRunning) return;
        if (MessageBox.Show("将终止临时 TaskHost 及其 Reasonix 子进程。已写入的项目文件不会删除，继续吗？", "停止 Reasonix 任务", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        new ReasonixIntegrationService(settings.CodexRoot, appPaths).StopTask(selectedReasonixTask);
        RequestReasonixTaskRefresh(manual: true);
    }

    private async void ApplyDeepSeekReasoningEffortButton_Click(object sender, RoutedEventArgs e)
    {
        var effort = SelectedDeepSeekReasoningEffort();
        if (MessageBox.Show("将更新 DeepSeek 子智能体的思考强度。需要先安全退出 Codex；设置只影响之后新建的子智能体，继续吗？", "应用思考强度", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!await EnsureCodexStoppedAsync()) return;
        var success = await RunOperationAsync("应用 DeepSeek 思考强度", cancellationToken => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            new ApiProviderService(settings.CodexRoot, appPaths, processService).UpdateDeepSeekPlanWorkerReasoningEffort(effort);
        }, cancellationToken), showProgress: false);
        await RefreshSubagentSettingsAsync();
        if (success) MessageBox.Show("已保存。请重新打开 Codex，新建的 DeepSeek 子智能体将使用所选强度。", "设置完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private string SelectedDeepSeekReasoningEffort() => (DeepSeekReasoningEffortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "high";

    private void SelectDeepSeekReasoningEffort(string effort)
    {
        foreach (var item in DeepSeekReasoningEffortBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), effort, StringComparison.OrdinalIgnoreCase))
            {
                DeepSeekReasoningEffortBox.SelectedItem = item;
                return;
            }
        }
        DeepSeekReasoningEffortBox.SelectedIndex = 2;
    }

    private void ProjectsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProjectActionButtons();

    /// <summary>项目页：“加入备份/不再备份”依赖选择，无选择时禁用。</summary>
    private void UpdateProjectActionButtons()
    {
        var hasSelection = ProjectsGrid.SelectedItems.Count > 0;
        ProtectProjectsButton.IsEnabled = hasSelection;
        UnprotectProjectsButton.IsEnabled = hasSelection;
    }

    private void SnapshotsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RestoreSnapshotButton.IsEnabled = SnapshotsGrid.SelectedItem is SnapshotSummary;

    private void DshSnapshotsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RestoreDshSnapshotButton.IsEnabled = DshSnapshotsGrid.SelectedItem is SnapshotSummary;

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
            RestoreSnapshotButton.IsEnabled = false;
            return;
        }
        try
        {
            var repository = new BackupRepository(settings.BackupRepositoryPath);
            SnapshotsGrid.ItemsSource = repository.ListSnapshots()
                .Where(snapshot => !SnapshotContainsOnlyDshData(repository, snapshot.Id))
                .ToList();
        }
        catch { SnapshotsGrid.ItemsSource = Array.Empty<SnapshotSummary>(); }
        RestoreSnapshotButton.IsEnabled = SnapshotsGrid.SelectedItem is SnapshotSummary;
    }

    /// <summary>
    /// 刷新 Harness 页的 DSH 快照列表：只显示 manifest 中包含 dsh- 数据源的快照，
    /// 不显示普通 Codex/项目快照；空仓库、无 DSH 快照或仓库不可读时给出友好提示。
    /// </summary>
    private void RefreshDshSnapshots()
    {
        if (DshSnapshotsGrid is null) return;
        var repositoryPath = settings.BackupRepositoryPath;
        if (string.IsNullOrWhiteSpace(repositoryPath) || !File.Exists(Path.Combine(repositoryPath, "repository.json")))
        {
            DshSnapshotsGrid.ItemsSource = Array.Empty<SnapshotSummary>();
            DshSnapshotListHintText.Text = "尚未设置备份仓库。请选择备份位置后创建 DSH 快照。";
            DshSnapshotListHintText.Visibility = Visibility.Visible;
            RestoreDshSnapshotButton.IsEnabled = false;
            return;
        }
        try
        {
            var repository = new BackupRepository(repositoryPath);
            var dshSnapshots = repository.ListSnapshots()
                .Where(snapshot => SnapshotContainsDshData(repository, snapshot.Id))
                .ToList();
            DshSnapshotsGrid.ItemsSource = dshSnapshots;
            DshSnapshotListHintText.Visibility = dshSnapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DshSnapshotListHintText.Text = dshSnapshots.Count == 0
                ? "暂无 DSH 快照。点击“立即备份 DSH”创建包含 DSH Skills、Agent 预设、用户配置与插件的快照。"
                : $"共 {dshSnapshots.Count} 个 DSH 快照，仅显示包含 DSH 数据的快照。";
        }
        catch
        {
            DshSnapshotsGrid.ItemsSource = Array.Empty<SnapshotSummary>();
            DshSnapshotListHintText.Text = "无法读取备份仓库中的 DSH 快照列表。";
            DshSnapshotListHintText.Visibility = Visibility.Visible;
        }
        RestoreDshSnapshotButton.IsEnabled = DshSnapshotsGrid.SelectedItem is SnapshotSummary;
    }

    /// <summary>快照 manifest 是否包含 dsh- 数据源；单个快照读取失败视为不含 DSH 数据。</summary>
    private static bool SnapshotContainsDshData(BackupRepository repository, string snapshotId)
    {
        try
        {
            return repository.LoadManifest(snapshotId).Sources
                .Any(source => source.Id.StartsWith(DshExtensionBackupService.SourceIdPrefix, StringComparison.Ordinal));
        }
        catch { return false; }
    }

    /// <summary>仅含 DSH 数据源的新式专属快照不混入通用列表；历史混合快照仍保留在通用列表。</summary>
    private static bool SnapshotContainsOnlyDshData(BackupRepository repository, string snapshotId)
    {
        try
        {
            var sources = repository.LoadManifest(snapshotId).Sources;
            return sources.Count > 0 && sources.All(source =>
                source.Id.StartsWith(DshExtensionBackupService.SourceIdPrefix, StringComparison.Ordinal));
        }
        catch { return false; }
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
            ["Collaboration"] = CollaborationPage,
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
            ["Collaboration"] = CollaborationNav,
            ["Projects"] = ProjectsNav,
            ["Snapshots"] = SnapshotsNav,
            ["Migration"] = MigrationNav,
            ["Health"] = HealthNav,
            ["Settings"] = SettingsNav
        };
        if (navigation.TryGetValue(page, out var selectedNav) && selectedNav.IsChecked != true) selectedNav.IsChecked = true;
        foreach (var pair in pages) pair.Value.Visibility = pair.Key == page ? Visibility.Visible : Visibility.Collapsed;
        settings.LastSelectedPage = page;
        // 进入协作页不自动扫描任务或探测外部执行器（历史任务较多或外部 CLI 异常时，
        // 自动工作会让导航看起来像卡死）；用户可用页面内“刷新任务/重新检测”明确触发。
        // PageVisible 仅用于门控“页面可见时”的自动刷新（Harness 任务刷新为纯文件读取）。
        taskRefreshCoordinator.PageVisible = page == "Collaboration";
        if (page == "Collaboration" && SelectedCollaborationMode() == CollaborationMode.Harness)
            RequestHarnessTaskRefresh(manual: false);
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
                RefreshDshSnapshots();
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

    /// <summary>
    /// Harness 页“立即备份 DSH”：只备份 DshExtensionBackupService.DiscoverSources() 返回的
    /// DSH Skills、Agent 预设、用户配置与用户插件；未安装 DSH 或无数据时给出清晰中文提示，
    /// 不加入任何 Codex/项目数据源。创建后刷新通用与 DSH 两套快照列表。
    /// </summary>
    private async void BackupDshNow_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(settings.BackupRepositoryPath))
        {
            MessageBox.Show("请先选择备份仓库位置。", "尚未设置备份仓库", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        IReadOnlyList<BackupSource> dshSources;
        try { dshSources = new DshExtensionBackupService().DiscoverSources(); }
        catch (Exception ex) { ShowError(ex); return; }
        if (dshSources.Count == 0)
        {
            MessageBox.Show("未检测到 DeepSeek Harness 数据。请确认已安装 DeepSeek Harness（DSH Home 存在），且 skills、.agent-presets、profiles 或用户插件目录中有可备份内容。", "没有 DSH 数据", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunOperationAsync("备份 DSH 数据", async cancellationToken =>
        {
            var repository = new BackupRepository(settings.BackupRepositoryPath);
            var result = await repository.CreateSnapshotAsync("DSH 数据保护", dshSources, CreateProgress(), cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                RefreshSnapshots();
                RefreshDshSnapshots();
                RefreshDashboard();
                MessageBox.Show($"DSH 快照完成：{result.Summary.FileCount} 个文件，结果 {result.Summary.Outcome}。\n新增存储：{FormatBytes(result.Summary.NewStoredBytes)}", "DSH 备份完成", MessageBoxButton.OK, result.Summary.Outcome == OperationOutcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            });
        });
    }

    /// <summary>
    /// 把所选 DSH 快照中的 DSH（DeepSeek Harness）Skills、Agent 预设、用户配置与插件安全恢复到
    /// 当前设备的 DSH Home：先显示数据种类与目标并确认，恢复时先解密到临时文件并验证哈希，
    /// 再按数据源映射落位（绝不用快照中的旧用户名绝对路径）；默认不覆盖已有文件，
    /// 越界、符号链接/重解析点与非法插件名由 Core 服务拒绝。恢复后刷新两套快照列表。
    /// </summary>
    private async void RestoreDshSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (DshSnapshotsGrid.SelectedItem is not SnapshotSummary snapshot)
        {
            MessageBox.Show("请先在 DSH 快照列表中选择一个要恢复的快照。", "未选择 DSH 快照", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dsh = new DshExtensionBackupService();
        SnapshotManifest manifest;
        try { manifest = new BackupRepository(settings.BackupRepositoryPath).LoadManifest(snapshot.Id); }
        catch (Exception ex) { ShowError(ex); return; }
        DshExtensionBackupService.DshRestorePlan plan;
        try { plan = dsh.BuildRestorePlan(manifest); }
        catch (InvalidOperationException ex) { MessageBox.Show(ex.Message, "没有 DSH 数据", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        catch (Exception ex) { ShowError(ex); return; }
        try { DshExtensionBackupService.EnsureTargetOutsideRepository(settings.BackupRepositoryPath, plan.TargetHome); }
        catch (Exception ex) { ShowError(ex); return; }
        var kinds = string.Join("\n", plan.Kinds.Select(kind => "· " + kind));
        if (MessageBox.Show($"将恢复到当前设备的 DSH Home：\n{plan.TargetHome}\n\n包含：\n{kinds}\n\n不含账号密钥、会话和附件；已存在的文件不会被覆盖。\n继续吗？", "恢复所选 DSH 快照", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunOperationAsync("恢复 DSH 数据", async cancellationToken =>
        {
            var repository = new BackupRepository(settings.BackupRepositoryPath);
            var result = await repository.RestoreAsync(new RestoreRequest(snapshot.Id, plan.TargetHome, plan.SourceIds, SourceTargetMap: plan.SourceTargetMap), CreateProgress(), cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                RefreshSnapshots();
                RefreshDshSnapshots();
                MessageBox.Show($"已恢复 {result.RestoredFiles} 个文件到：\n{plan.TargetHome}\n结果：{result.Outcome}\n\n请重启 DSH / Harness Host 使设置生效。", "DSH 恢复完成", MessageBoxButton.OK, result.Outcome == OperationOutcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            });
        });
    }

    /// <summary>
    /// Harness 页“组件迁移与配置”：启动一轮 DSH 组件扫描。单一真正异步流程：扫描 I/O 在后台
    /// 执行，UI 线程只更新状态；手动触发时取消进行中的旧扫描并启动新扫描（推荐取消后重启），
    /// 旧轮不再更新 UI。扫描期间禁用“重新扫描”并显示“正在扫描（已用时 N 秒）”，
    /// 完成显示组件数与实际耗时，取消/失败恢复按钮并给出清楚状态；关闭窗口时取消扫描。
    /// </summary>
    private void RefreshDshComponents()
    {
        if (taskRefreshCoordinator.Closed) return;
        dshScanCts?.Cancel();
        dshScanCts?.Dispose();
        dshScanCts = new CancellationTokenSource();
        var token = dshScanCts.Token;
        _ = RunDshComponentScanAsync(token);
    }

    private async Task RunDshComponentScanAsync(CancellationToken token)
    {
        DshRescanButton.IsEnabled = false;
        DshComponentsHintText.Text = "正在扫描 DSH 组件…";
        var stopwatch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (taskRefreshCoordinator.Closed || token.IsCancellationRequested) return;
            DshComponentsHintText.Text = $"正在扫描 DSH 组件（已用时 {Math.Max(0, (int)stopwatch.Elapsed.TotalSeconds)} 秒）…";
        };
        timer.Start();

        IReadOnlyList<DshComponentInfo>? scanned = null;
        string? failureMessage = null;
        var cancelled = false;
        try
        {
            scanned = await Task.Run(() => new DshComponentScanner().Scan(token), token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { failureMessage = ex.Message; }
        finally
        {
            timer.Stop();
            stopwatch.Stop();
        }

        // 关闭窗口后不得继续写入已关闭窗口；只允许最新一轮更新 UI（旧轮被新扫描替换时直接退出）。
        if (taskRefreshCoordinator.Closed || !IsLoaded) return;
        if (dshScanCts is null || dshScanCts.Token != token) return;
        DshRescanButton.IsEnabled = true;
        if (cancelled || token.IsCancellationRequested)
        {
            DshComponentsHintText.Text = "扫描已取消。";
            return;
        }
        if (failureMessage is not null)
        {
            DshComponentsHintText.Text = "组件扫描失败：" + failureMessage;
            return;
        }
        dshComponents = scanned!;
        DshComponentsList.ItemsSource = dshComponents;
        DshComponentsHintText.Text = dshComponents.Count == 0
            ? "未检测到 DSH 组件（DSH Home 不存在或目录为空）。"
            : $"扫描完成：共 {dshComponents.Count} 个组件，用时 {stopwatch.Elapsed.TotalSeconds:0.#} 秒：{dshComponents.Count(component => component.Status == DshComponentStatus.SetupNone)} 个无需配置，{dshComponents.Count(component => component.Status == DshComponentStatus.Ready)} 个已就绪，{dshComponents.Count(component => component.Status == DshComponentStatus.RequiredMissing)} 个待配置，{dshComponents.Count(component => component.Status == DshComponentStatus.OptionalConfig)} 个可选配置，{dshComponents.Count(component => component.Status is DshComponentStatus.ManualReview or DshComponentStatus.InvalidDeclaration)} 个待人工确认/声明无效。";
        DshComponentDetailText.Visibility = Visibility.Collapsed;
    }

    private void RefreshDshComponents_Click(object sender, RoutedEventArgs e) => RefreshDshComponents();

    /// <summary>
    /// 导出 DSH 迁移包：先扫描组件，保存对话框默认文件名带当前 Helper 版本；
    /// 导出前展示类型与内容预览（组件、文件数、大小），导出过程异步进行。
    /// </summary>
    private async void ExportDshTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (dshComponents.Count == 0)
        {
            DshComponentsHintText.Text = "尚未扫描到 DSH 组件，请先“重新扫描”。";
            return;
        }
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "4.1.0";
        var dialog = new SaveFileDialog
        {
            Title = "导出 DSH 迁移包（不含账号密钥、会话、附件与依赖树）",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = DshTransferService.BuildFileName(version),
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var preview = dshComponents
            .Where(component => Directory.Exists(component.RootPath))
            .Select(component =>
            {
                var files = Directory.Exists(component.RootPath)
                    ? Directory.EnumerateFiles(component.RootPath, "*", SearchOption.AllDirectories).Count()
                    : 0;
                return (component, files);
            })
            .ToList();
        var kinds = string.Join("\n", preview.GroupBy(item => item.component.Kind).Select(group => $"· {group.Key switch { DshComponentType.Skill => "Skills", DshComponentType.Plugin => "用户插件", DshComponentType.Preset => "Agent 预设", _ => group.Key.ToString() }}：{group.Count()} 个"));
        if (MessageBox.Show($"将导出 {preview.Count} 个 DSH 组件到：\n{dialog.FileName}\n\n{kinds}\n\n迁移包含配置声明副本；排除账号密钥、会话、附件、缓存、日志、临时文件与插件依赖树。\n继续吗？", "导出 DSH 迁移包", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunOperationAsync("导出 DSH 迁移包", async cancellationToken =>
        {
            var service = new DshTransferService(appPaths);
            var manifest = await service.ExportAsync(
                new DshTransferExportRequest(dialog.FileName, dshComponents.Where(component => Directory.Exists(component.RootPath)).ToList()),
                CreateProgress(), cancellationToken);
            await Dispatcher.InvokeAsync(() => MessageBox.Show($"DSH 迁移包已导出：\n{dialog.FileName}\n\n组件 {manifest.Components.Count} 个，文件 {manifest.Files.Count} 个。", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information));
        });
    }

    /// <summary>
    /// 导入 DSH 迁移包：先预览（校验结构、manifest 与哈希），展示组件清单与内容摘要，
    /// 确认后暂存到 Helper 临时目录并受控复制到当前设备 DSH Home；默认不覆盖已有文件，
    /// 失败回滚不留下半导入状态。
    /// </summary>
    private async void ImportDshTransfer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 DSH 迁移包（codex-helper-dsh-transfer-v*.zip）",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        DshTransferPreview preview;
        try { preview = await new DshTransferService(appPaths).PreviewAsync(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show("迁移包校验失败：" + ex.Message, "无法预览", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        var kinds = string.Join("\n", preview.Manifest.Components.GroupBy(component => component.Kind).Select(group => $"· {group.Key}：{group.Count()} 个"));
        var targetHome = new DshComponentScanner().DshHome;
        if (MessageBox.Show($"迁移包内容预览：\n{dialog.FileName}\n\n{kinds}\n文件 {preview.Manifest.Files.Count} 个，大小 {FormatBytes(preview.Manifest.Files.Sum(file => file.Length))}\n\n将导入到当前设备 DSH Home：\n{targetHome}\n\n已存在的文件不会被覆盖（保留本机）。\n继续吗？", "导入 DSH 迁移包", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunOperationAsync("导入 DSH 迁移包", async cancellationToken =>
        {
            var service = new DshTransferService(appPaths);
            var result = await service.ImportAsync(
                new DshTransferImportRequest(dialog.FileName, targetHome, OnlyNewFiles: true),
                CreateProgress(), cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                RefreshDshComponents();
                RefreshDshSnapshots();
                MessageBox.Show($"导入完成：已导入 {result.ImportedFiles} 个文件，跳过冲突 {result.SkippedConflicts} 个。\n结果：{result.Outcome}\n\n请重启 DSH / Harness Host 使设置生效。", "DSH 迁移导入完成", MessageBoxButton.OK, result.Outcome == OperationOutcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            });
        });
    }

    /// <summary>选中组件后展开配置步骤、缺失字段（只含 id）与判定依据；不显示任何密钥值。</summary>
    private void DshComponentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DshComponentDetailText is null) return;
        if (DshComponentsList.SelectedItem is not DshComponentInfo component)
        {
            DshComponentDetailText.Visibility = Visibility.Collapsed;
            return;
        }
        var parts = new List<string>
        {
            $"名称：{component.Name}（{component.KindDisplay}）",
            $"状态：{component.StatusDisplay}（{component.DeclarationSourceDisplay}）"
        };
        if (!string.IsNullOrWhiteSpace(component.Version)) parts.Add("版本：" + component.Version);
        if (component.MissingFieldIds.Count > 0)
            parts.Add("缺失配置字段：" + string.Join("、", component.MissingFieldIds));
        if (!string.IsNullOrWhiteSpace(component.ReviewReason)) parts.Add("原因：" + component.ReviewReason);
        if (component.Evidence.Count > 0) parts.Add("判定依据：" + string.Join("；", component.Evidence.Take(4)));
        if (!string.IsNullOrWhiteSpace(component.SetupSteps)) parts.Add("配置步骤：\n" + component.SetupSteps);
        DshComponentDetailText.Text = string.Join("\n", parts);
        DshComponentDetailText.Visibility = Visibility.Visible;
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
            if (kind == ConnectionKind.ResponsesSubagent) kind = ConnectionKind.CustomApi;
            new ApiProviderService(settings.CodexRoot, appPaths, processService).SaveProfile(ApiLabelBox.Text, kind, ApiUrlBox.Text, ApiModelBox.Text, ApiKeyBox.Password);
            ApiKeyBox.Clear(); RefreshConnections(); RefreshDashboard();
            MessageBox.Show("API 档案已使用 DPAPI 加密保存。切换后将作为 Codex 主模型使用。", "保存完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void SwitchConnection_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsGrid.SelectedItem is not ConnectionProfile profile) { MessageBox.Show("请选择一个连接档案。"); return; }
        if (profile.Kind == ConnectionKind.ResponsesSubagent)
        {
            MessageBox.Show("这是旧版本遗留的子智能体档案。请先点“修复旧 Responses 档案”，修复后可作为普通 Responses API 主模型使用。", "旧版档案", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
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
        if (ConnectionsGrid.SelectedItem is not ConnectionProfile { Kind: ConnectionKind.CustomApi or ConnectionKind.Sub2Api or ConnectionKind.ResponsesSubagent } profile) { MessageBox.Show("请选择 API 档案；旧版 Responses 档案也可以先进行连通性检测。"); return; }
        await RunOperationAsync("测试 API", async cancellationToken =>
        {
            var message = await new ApiProviderService(settings.CodexRoot, appPaths, processService).TestAsync(profile.Id, cancellationToken);
            await Dispatcher.InvokeAsync(() => { RefreshConnections(); MessageBox.Show(message, "检测通过", MessageBoxButton.OK, MessageBoxImage.Information); });
        });
    }

    private async void EnableDeepSeekPlanWorker_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsGrid.SelectedItem is not ConnectionProfile { Kind: ConnectionKind.CustomApi } profile)
        {
            MessageBox.Show("请选择一个 DeepSeek 官方 Responses API 档案。它只会成为编码子智能体，不会切换 GPT 主模型。", "启用 DeepSeek 开发协作", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        const string explanation = "将保留当前 GPT 主模型，并创建一个只负责编码的 DeepSeek 子智能体。GPT 会先选择明确的交接根目录，把完整任务和唯一指针写到该目录，校验后再用任务 ID 和指针绝对路径唤醒 DeepSeek 实施；这避免依赖可能丢失的子任务正文或错误的子工作区。启用前会安全备份 config、模型目录、子智能体配置和协作规则。";
        if (MessageBox.Show(explanation + "\n\n需要先安全退出 Codex，继续吗？", "启用 DeepSeek 开发协作", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!await EnsureCodexStoppedAsync()) return;
        var succeeded = await RunOperationAsync("启用 DeepSeek 开发协作", cancellationToken => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            new ApiProviderService(settings.CodexRoot, appPaths, processService).EnableDeepSeekPlanWorker(profile.Id, FindCredentialHelper());
        }, cancellationToken), showProgress: false);
        if (succeeded) MessageBox.Show("已启用。重新打开 Codex 后，GPT 主线程会规划与验收，DeepSeek 编码子智能体通过每次唯一指针的绝对路径读取任务并实施。", "设置完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void DisableDeepSeekPlanWorker_Click(object sender, RoutedEventArgs e)
    {
        var service = new ApiProviderService(settings.CodexRoot, appPaths, processService);
        if (!service.IsDeepSeekPlanWorkerEnabled())
        {
            MessageBox.Show("当前没有启用由 Codex Helper 管理的 DeepSeek 开发协作。", "关闭 DeepSeek 开发协作", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show("将只移除 Helper 创建的 DeepSeek 编码子智能体、其 provider 和协作规则；不会删除 API 档案、账号、项目或其他 Skills。需要先安全退出 Codex，继续吗？", "关闭 DeepSeek 开发协作", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!await EnsureCodexStoppedAsync()) return;
        var succeeded = await RunOperationAsync("关闭 DeepSeek 开发协作", cancellationToken => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            new ApiProviderService(settings.CodexRoot, appPaths, processService).DisableDeepSeekPlanWorker();
        }, cancellationToken), showProgress: false);
        if (succeeded) MessageBox.Show("已关闭 DeepSeek 开发协作，GPT 主模型和 API 档案均未改变。", "设置完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void RepairLegacyResponses_Click(object sender, RoutedEventArgs e)
    {
        if (ConnectionsGrid.SelectedItem is ConnectionProfile { Kind: ConnectionKind.ResponsesSubagent } cleanupProfile)
        {
            const string explanation = "旧版本曾把第三方 Responses API 配置为 Codex 原生子智能体，可能导致任务正文无法送达。此操作会清理旧配置，并把所选档案转换为普通 Responses API 档案；API Key 和档案都会保留，也不会自动切换主模型。";
            if (MessageBox.Show(explanation + "\n\n继续修复吗？", "修复旧 Responses 档案", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            if (!await EnsureCodexStoppedAsync()) return;
            string? report = null;
            var cleaned = await RunOperationAsync("修复旧 Responses 档案", cancellationToken => Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                report = new ApiProviderService(settings.CodexRoot, appPaths, processService).CleanUnsupportedNativeSubagent(cleanupProfile.Id);
            }, cancellationToken), showProgress: false);
            if (cleaned)
            {
                RefreshConnections();
                MessageBox.Show(report ?? explanation, "修复完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }
        MessageBox.Show("请选择列表中标记为“旧版子智能体档案”的连接。普通 API 档案不需要修复。", "修复旧 Responses 档案", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private async void RefreshDeepSeekCacheStats_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("刷新 DeepSeek 缓存统计", async cancellationToken =>
        {
            var lookback = DeepSeekCacheRangeToLookback(SelectedDeepSeekCacheRange());
            var stats = await new DeepSeekCacheStatsService(settings.CodexRoot, appPaths.ReasonixTasksDirectory).ReadAsync(lookback, cancellationToken);
            await Dispatcher.InvokeAsync(() => DeepSeekCacheStatsText.Text = stats.ToDisplayText());
        }, showProgress: false);
    }

    private string SelectedDeepSeekCacheRange() => (DeepSeekCacheRangeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "14d";

    private void SelectDeepSeekCacheRange(string range)
    {
        // 程序化设置选中项会触发 SelectionChanged；抑制它，避免初始化意外改写已保存的缓存范围。
        suppressCacheRangeSelection = true;
        try
        {
            foreach (var item in DeepSeekCacheRangeBox.Items.OfType<ComboBoxItem>())
                if (string.Equals(item.Tag?.ToString(), range, StringComparison.OrdinalIgnoreCase)) { DeepSeekCacheRangeBox.SelectedItem = item; return; }
            DeepSeekCacheRangeBox.SelectedItem = DeepSeekCacheRangeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag?.ToString(), "14d", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            suppressCacheRangeSelection = false;
        }
    }

    /// <summary>范围 → lookback；all 返回 null（全量）；非法值回退 14 天。</summary>
    private static TimeSpan? DeepSeekCacheRangeToLookback(string range) => range switch
    {
        "24h" => TimeSpan.FromHours(24),
        "7d" => TimeSpan.FromDays(7),
        "30d" => TimeSpan.FromDays(30),
        "all" => null,
        _ => TimeSpan.FromDays(14)
    };

    private void DeepSeekCacheRangeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 构造默认选中项时 settings 尚未就绪且不应持久化；初始化设置选中项时同样不保存。
        // 仅当窗口已加载且为用户实际切换时才持久化，避免构造期 NullReferenceException。
        if (settings is null || !IsLoaded || suppressCacheRangeSelection) return;
        settings.DeepSeekCacheRange = SelectedDeepSeekCacheRange();
        settingsService.Save(settings);
    }

    private async void BackfillReasonixStats_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show("修复历史统计只采用严格证据（session 模型、manifest executionModel/model、Review Packet 独立 Model 行）补写旧 Reasonix 状态模型；报告正文、当前默认模型、项目名、任务名不作为证据；无法确认会安全跳过，已补写会幂等跳过。继续吗？", "修复历史统计", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (confirm != MessageBoxResult.Yes) return;
        await RunOperationAsync("修复历史统计", async cancellationToken =>
        {
            var service = new DeepSeekCacheStatsService(settings.CodexRoot, appPaths.ReasonixTasksDirectory);
            var backfill = await Task.Run(() => service.BackfillReasonixExecutionModel(cancellationToken), cancellationToken);
            var stats = await service.ReadAsync(DeepSeekCacheRangeToLookback(SelectedDeepSeekCacheRange()), cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                BackfillResultText.Text = backfill.ToDisplayText();
                BackfillResultText.Visibility = Visibility.Visible;
                DeepSeekCacheStatsText.Text = stats.ToDisplayText();
            });
        }, showProgress: false);
    }

    private async void RetryReasonixTask_Click(object sender, RoutedEventArgs e)
    {
        if (selectedReasonixTask is null) return;
        var service = new ReasonixIntegrationService(settings.CodexRoot, appPaths);
        var blockReason = service.RetryBlockReason(selectedReasonixTask);
        if (blockReason is not null) { MessageBox.Show(blockReason, "无法重试", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var confirm = MessageBox.Show("将保留旧尝试证据并归档到 attempts/，从当前源码继续收尾；项目改动不会回滚。由 Helper 启动的重试无法自动唤醒既有 GPT 轮次，完成后请返回原 Codex 任务继续验收。继续吗？", "重试未完成任务", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        var result = await service.RetryTaskAsync(selectedReasonixTask);
        RequestReasonixTaskRefresh(manual: true);
        MessageBox.Show(result.Message, result.Success ? "已启动重试" : "无法重试", MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void ReturnToCodexTask_Click(object sender, RoutedEventArgs e)
    {
        if (selectedReasonixTask is null) return;
        var uri = CodexThreadUri.Build(selectedReasonixTask.ReturnUri, selectedReasonixTask.CodexThreadId);
        if (uri is null) { MessageBox.Show("没有可用的原 Codex 任务 URI。", "返回原 Codex 任务", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void CopyReasonixTaskId_Click(object sender, RoutedEventArgs e)
    {
        if (selectedReasonixTask is null) return;
        try
        {
            Clipboard.SetText(selectedReasonixTask.TaskId);
            MessageBox.Show($"已复制任务 ID：{selectedReasonixTask.TaskId}", "已复制", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenReasonixTaskDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (selectedReasonixTask is null || string.IsNullOrWhiteSpace(selectedReasonixTask.TaskDirectory)) return;
        try
        {
            if (Directory.Exists(selectedReasonixTask.TaskDirectory))
                Process.Start(new ProcessStartInfo { FileName = selectedReasonixTask.TaskDirectory, UseShellExecute = true });
            else
                MessageBox.Show("任务目录不存在：" + selectedReasonixTask.TaskDirectory, "无法打开", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    /// <summary>状态 → 颜色：完成绿 / 执行中蓝 / 待开始灰 / 失败红 / 其他默认。</summary>
    private Brush StatusBrush(string key) => key switch
    {
        "completed" => new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
        "running" => new SolidColorBrush(Color.FromRgb(0x15, 0x65, 0xC0)),
        "pending" => new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
        "failed" => new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)),
        _ => (Brush)FindResource("SecondaryTextBrush")
    };

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
        UpdateProjectActionButtons();
        RefreshDashboard();
    }

    private async void ProtectProjects_Click(object sender, RoutedEventArgs e)
    {
        foreach (var project in ProjectsGrid.SelectedItems.OfType<ProjectInfo>())
            if (!settings.ProtectedProjectPaths.Contains(project.Path, StringComparer.OrdinalIgnoreCase)) settings.ProtectedProjectPaths.Add(project.Path);
        settingsService.Save(settings);
        projects = await projectDiscovery.DiscoverAsync(settings.WorkspaceRoots, settings.ProtectedProjectPaths);
        ProjectsGrid.ItemsSource = projects; UpdateProjectActionButtons(); RefreshDashboard();
    }

    private async void UnprotectProjects_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProjectsGrid.SelectedItems.OfType<ProjectInfo>().Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        settings.ProtectedProjectPaths.RemoveAll(selected.Contains);
        settingsService.Save(settings);
        projects = await projectDiscovery.DiscoverAsync(settings.WorkspaceRoots, settings.ProtectedProjectPaths);
        ProjectsGrid.ItemsSource = projects; UpdateProjectActionButtons(); RefreshDashboard();
    }

    private void ChooseRepository_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择或创建 Codex Helper 备份仓库目录", InitialDirectory = Directory.Exists(settings.BackupRepositoryPath) ? settings.BackupRepositoryPath : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var protectedSources = BuildBackupSources();
            protectedSources.AddRange(new DshExtensionBackupService().DiscoverSources());
            PathSafety.EnsureRepositoryOutsideSources(dialog.FolderName, protectedSources.Select(item => item.Path));
            settings.BackupRepositoryPath = dialog.FolderName;
            settingsService.Save(settings);
            new BackupRepository(dialog.FolderName).Initialize();
            RepositoryPathText.Text = dialog.FolderName;
            DshRepositoryPathText.Text = dialog.FolderName;
            RefreshSnapshots(); RefreshDshSnapshots(); RefreshDashboard();
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
        if (ExportDshCheck.IsChecked == true)
        {
            // DSH Skills、Agent 预设、用户配置与插件：与一键备份使用相同发现规则；
            // 未安装 DSH 时自动不添加任何项。
            try
            {
                foreach (var dshSource in new DshExtensionBackupService().DiscoverSources())
                    result.Add(new BundleExportItem(dshSource.Id, "dsh", dshSource.DisplayName, dshSource.Path));
            }
            catch
            {
                // DSH 发现失败不影响其余导出项。
            }
        }
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

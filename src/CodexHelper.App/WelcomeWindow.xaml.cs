using System.Windows;

namespace CodexHelper.App;

public partial class WelcomeWindow : Window
{
    public event EventHandler<string>? NavigateRequested;
    private int currentStep;
    private readonly GuideStep[] steps =
    [
        new("Dashboard", "总览：先看现在是否安全", "总览不是设置页，而是提醒你目前的连接、项目保护和最近备份状态。", "打开软件后先看三张卡片：当前连接、受保护项目、最近备份。红色或“尚未设置”表示还需要完成后面的步骤。", "不用急着一次设置完。建议先完成“选项目”和“备份位置”，再创建第一次备份。"),
        new("Connections", "连接中心：保存登录或 API", "这里把官方登录、普通 API 和 Sub2API 放在一起管理。", "已经在 Codex 登录官方账号时，填写一个好记的名称（如“个人账号”）后点“保存当前登录”。使用 API 时，填写名称、Base URL、模型和 API Key。切换连接前请先关闭 Codex。", "API Key 不会写入 config.toml；连接档案导入后也不会自动切换。"),
        new("Projects", "选择要保护的项目：从哪里找项目", "这里选择的是“查找项目的位置”，不是备份保存位置。", "选择放着多个项目的上级文件夹，例如 C:\\实用软件开发 或 D:\\Projects。软件会扫描这个目录本身和第一层子目录，找到 Git、.sln、package.json、pyproject.toml 等项目。选中要长期保护的项目后点“加入备份”。", "如果项目在更深层，选择它的直接上级目录，或直接选择这个项目目录。"),
        new("Snapshots", "备份与恢复：备份放到哪里", "这里选择的是“备份保存位置”，与上一步完全不同。", "选择一个专门放备份的文件夹，例如 D:\\CodexHelperBackup 或移动硬盘 E:\\Backup\\CodexHelper。之后点“立即备份”；第一次会创建基线，以后只保存变化内容。", "不要把备份位置放进项目目录、项目子目录、.codex 或 .codex 子目录，否则会造成备份循环。"),
        new("Migration", "迁移中心：换电脑时使用", "迁移中心适合换电脑、重装系统或手动交接；它不同于日常快照。", "导出时选择需要的 Skills、配置、项目和连接档案，设置至少 10 位迁移口令，生成 .chbundle。新电脑上先“预览”，普通文件导入新目录，连接档案单独导入本机保险库。", "迁移口令无法找回；连接导入后请到“连接中心”手动确认和切换。"),
        new("Health", "健康中心：遇到异常先检查", "健康中心会检查 Codex 目录、配置、登录状态、运行中的进程和备份仓库，不会显示你的密钥或登录内容。", "切换账号、API 或进行原位恢复前，如果遇到异常提示，先点击“运行健康检查”。它会告诉你 Codex 是否仍在运行、配置文件是否可读取，以及备份仓库能否正常使用。", "显示 Codex 正在运行时，先关闭 Codex，再进行切换连接或原位恢复。"),
        new("Settings", "设置：确认数据位置和备份范围", "这里用于确认 Codex 数据在哪，以及日常备份中要不要包含任务、附件和生成图片。", "通常只需确认“Codex 数据目录”是当前正在使用的 .codex 文件夹。需要更完整的保护时勾选任务、附件或生成图片；如果不需要，可取消勾选以减小备份体积。", "以后想再看这套说明，随时在“设置”点击“重新观看新手引导”。")
    ];

    public WelcomeWindow()
    {
        InitializeComponent();
        GuideProgress.Maximum = steps.Length;
        UpdateStep();
    }

    public void NavigateToCurrentStep() => NavigateRequested?.Invoke(this, steps[currentStep].Page);

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (currentStep == 0) return;
        currentStep--;
        UpdateStep();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (currentStep < steps.Length - 1)
        {
            currentStep++;
            UpdateStep();
            return;
        }
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateStep()
    {
        var step = steps[currentStep];
        StepIndicatorText.Text = $"第 {currentStep + 1} / {steps.Length} 步";
        GuideProgress.Value = currentStep + 1;
        StepTitleText.Text = step.Title;
        StepSummaryText.Text = step.Summary;
        StepBodyText.Text = step.Body;
        StepTipText.Text = "提示：" + step.Tip;
        PreviousButton.IsEnabled = currentStep > 0;
        NextButton.Content = currentStep == steps.Length - 1 ? "开始使用" : "下一步";
        NavigateToCurrentStep();
    }

    private sealed record GuideStep(string Page, string Title, string Summary, string Body, string Tip);
}

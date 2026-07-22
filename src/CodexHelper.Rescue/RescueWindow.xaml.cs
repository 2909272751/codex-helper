using System.Windows;
using Microsoft.Win32;
using CodexHelper.Core.Models;
using CodexHelper.Core.Services;
using CodexHelper.Core.Infrastructure;

namespace CodexHelper.Rescue;

public partial class RescueWindow : Window
{
    private BackupRepository? repository;
    private readonly AppLogger logger = new(new AppPaths());

    public RescueWindow() => InitializeComponent();

    private void ChooseRepository_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择包含 repository.json 的 Codex Helper 备份仓库" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            repository = new BackupRepository(dialog.FolderName);
            var snapshots = repository.ListSnapshots();
            RepositoryBox.Text = dialog.FolderName;
            SnapshotGrid.ItemsSource = snapshots;
            StatusText.Text = $"仓库已解锁，共 {snapshots.Count} 个快照。";
        }
        catch (Exception ex) { ShowError("无法打开仓库", ex); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (repository is null || SnapshotGrid.SelectedItem is not SnapshotSummary snapshot) { MessageBox.Show("请先打开仓库并选择快照。"); return; }
        var dialog = new OpenFolderDialog { Title = "选择新的恢复目录" };
        if (dialog.ShowDialog(this) != true) return;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        StatusText.Text = "正在解密、恢复并校验文件…";
        try
        {
            var result = await repository.RestoreAsync(new RestoreRequest(snapshot.Id, dialog.FolderName));
            StatusText.Text = $"恢复完成：{result.RestoredFiles} 个文件，结果 {result.Outcome}。";
            MessageBox.Show($"已恢复到：\n{dialog.FolderName}", "恢复完成", MessageBoxButton.OK, result.Outcome == OperationOutcome.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) { StatusText.Text = "恢复失败"; ShowError("恢复失败", ex); }
        finally { Progress.Visibility = Visibility.Collapsed; Progress.IsIndeterminate = false; }
    }

    private void ShowError(string operation, Exception ex)
    {
        var errorId = logger.WriteError(operation, ex);
        MessageBox.Show(ex.Message + $"\n\n错误编号：{errorId}\n详细信息已写入本机日志。", operation, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

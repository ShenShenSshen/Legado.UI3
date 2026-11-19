using Legado.UI3.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Legado.UI3;

public sealed partial class MainWindow
{
    private bool checkingUpdate;
    private bool startupCheckStarted;

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (checkingUpdate || closing || !manual && startupCheckStarted) return;
        startupCheckStarted = true;
        checkingUpdate = true;
        try
        {
            if (manual) ShowNotice("正在检查更新…");
            var release = await Updates.CheckAsync(CancellationToken.None);
            if (closing || root.XamlRoot is null) return;
            if (release.Version <= Updates.CurrentVersion)
            {
                if (manual) ShowNotice("当前已是最新版本。");
                return;
            }
            if (manual && notice.Message == "正在检查更新…") notice.IsOpen = false;
            var prompt = Ui.Text($"v{Updates.CurrentVersion} → v{release.Version}\n下载校验后，应用会保存进度并自动重启。", 14);
            if (await DialogAsync("发现新版本", prompt, "下载并更新", defaultButton: ContentDialogButton.Close) == ContentDialogResult.Primary)
                await DownloadAndRestartAsync(release);
        }
        catch (OperationCanceledException) { if (manual && !closing) ShowNotice("检查更新超时，请稍后重试。", true); }
        catch (Exception error)
        {
            if (manual && !closing) ReportError(error);
        }
        finally { checkingUpdate = false; }
    }

    private async Task DownloadAndRestartAsync(UpdateRelease release)
    {
        using var cancel = new CancellationTokenSource();
        var status = Ui.Text("正在连接…", 14);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot, Title = $"正在更新至 v{release.Version}",
            Content = status, CloseButtonText = "取消下载"
        };
        var closingDialog = false;
        var finished = false;
        dialog.Closing += (_, _) => { closingDialog = true; if (!finished) cancel.Cancel(); };
        var showing = dialog.ShowAsync();
        var progress = new Progress<string>(message => { if (!closingDialog) status.Text = message; });
        string? candidate = null;
        try { candidate = await UpdateInstall.StageAsync(store.Root, release, progress, cancel.Token); }
        catch (OperationCanceledException)
        { if (!closing) ShowNotice(cancel.IsCancellationRequested ? "下载已取消。" : "下载超时，请重试。", !cancel.IsCancellationRequested); }
        catch (Exception error) { if (!closing) ReportError(error); }
        finally
        {
            finished = true;
            if (!closingDialog) dialog.Hide();
            await showing;
        }
        if (candidate is not null && !cancel.IsCancellationRequested)
            await ExitAsync(() => UpdateInstall.StartAfterExit(candidate));
    }
}

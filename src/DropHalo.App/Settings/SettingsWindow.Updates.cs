using System.Runtime.InteropServices;
using System.Text.Json;
using DropHalo.App.Diagnostics;
using DropHalo.App.Updates;
using DropHalo.Core.Settings;
using DropHalo.Core.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DropHalo.App.Settings;

internal sealed partial class SettingsWindow
{
    private readonly HttpClient _updateHttp = UpdateClient.CreateHttpClient();
    private readonly ToggleSwitch _strictOffline = new() { Header = L.T("严格离线"), OnContent = L.T("开"), OffContent = L.T("关") };
    private readonly ToggleSwitch _autoUpdates = new() { Header = L.T("自动检查更新"), OnContent = L.T("开"), OffContent = L.T("关") };
    private readonly ComboBox _updateSource = new() { Header = L.T("更新源"), Items = { "CNB", "GitHub" } };
    private readonly TextBlock _updateStatus = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly Button _checkUpdate = new() { Content = L.T("检查更新") };
    private readonly Button _installUpdate = new() { Content = L.T("下载并安装"), Visibility = Visibility.Collapsed };
    private readonly Button _cancelUpdate = new() { Content = L.T("取消"), Visibility = Visibility.Collapsed };
    private UpdateRelease? _availableUpdate;
    private CancellationTokenSource? _updateCancellation;
    private DispatcherQueueTimer? _updateTimer;
    private bool _updateBusy;
    private bool _showingUpdateSettings;
    private Func<bool> _hasJobs = () => false;
    private Action _updateShutdown = () => { };
    private Action<string> _updateNotify = _ => { };
    private static string UpdateArchitecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    private string UpdateDataDirectory => App.DataDir(Environment.GetCommandLineArgs());
    private static Version CurrentVersion => typeof(App).Assembly.GetName().Version!;

    private StackPanel BuildUpdates()
    {
        AutomationProperties.SetAutomationId(_strictOffline, "strict-offline");
        AutomationProperties.SetAutomationId(_autoUpdates, "auto-updates");
        AutomationProperties.SetAutomationId(_updateSource, "update-source");
        AutomationProperties.SetAutomationId(_checkUpdate, "check-update");
        AutomationProperties.SetAutomationId(_installUpdate, "install-update");
        AutomationProperties.SetAutomationId(_updateStatus, "update-status");
        _checkUpdate.Click += async (_, _) => await CheckUpdatesAsync(manual: true).ConfigureAwait(true);
        _installUpdate.Click += async (_, _) => await InstallUpdateAsync().ConfigureAwait(true);
        _cancelUpdate.Click += (_, _) => _updateCancellation?.Cancel();
        _strictOffline.Toggled += (_, _) => SaveUpdateSettings();
        _autoUpdates.Toggled += (_, _) => SaveUpdateSettings();
        _updateSource.SelectionChanged += (_, _) => SaveUpdateSettings();
        _updateStatus.Text = L.T("当前版本：{0}", CurrentVersion.ToString(3));
        var resultPath = Path.Combine(UpdateDataDirectory, "update-result.json");
        try
        {
            if (File.Exists(resultPath))
            {
                using var result = JsonDocument.Parse(File.ReadAllText(resultPath).TrimStart('\uFEFF'));
                if (!result.RootElement.GetProperty("Success").GetBoolean())
                {
                    _updateStatus.Text = L.T("上次更新未完成，原版本已保留。请重试；详情见设置文件夹中的 update-result.json。");
                }
                else if (result.RootElement.TryGetProperty("RebootRequired", out var reboot) && reboot.GetBoolean())
                {
                    _updateStatus.Text = L.T("更新已安装。Windows 提示需要重启电脑才能完成，请保存工作后自行重启。");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or KeyNotFoundException)
        {
            FileLog.Error("update: could not read the last install result", ex);
        }

        return Section(L.T("应用更新"), L.T("启动后及每 24 小时检查正式版。发现新版本会通知你，确认后下载、验证签名、安装并重新打开 DropHalo。更新不会上传你的文件；MSI 安装需要管理员确认。"),
            _autoUpdates, _updateSource, _updateStatus,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _checkUpdate, _installUpdate, _cancelUpdate } });
    }

    public void StartUpdates(DispatcherQueue ui, Func<bool> hasJobs, Action shutdown, Action<string> notify)
    {
        _hasJobs = hasJobs;
        _updateShutdown = shutdown;
        _updateNotify = notify;
        _updateTimer = ui.CreateTimer();
        _updateTimer.Interval = TimeSpan.FromSeconds(45);
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            if (_settings.Current.Privacy is { StrictOffline: false, CheckForUpdates: true })
            {
                await CheckUpdatesAsync(manual: false).ConfigureAwait(true);
            }
        };
        _updateTimer.Start();
    }

    private void ShowUpdateSettings()
    {
        _showingUpdateSettings = true;
        _strictOffline.IsOn = _settings.Current.Privacy.StrictOffline;
        _autoUpdates.IsOn = _settings.Current.Privacy.CheckForUpdates;
        _updateSource.SelectedIndex = _settings.Current.UpdateSource == UpdateSource.GitHub ? 1 : 0;
        _showingUpdateSettings = false;
        UpdateControls();
    }

    private void SaveUpdateSettings()
    {
        if (_showingUpdateSettings || _updateSource.SelectedIndex < 0)
        {
            return;
        }

        var privacy = new PrivacySettings(_strictOffline.IsOn, _autoUpdates.IsOn);
        var source = _updateSource.SelectedIndex == 1 ? UpdateSource.GitHub : UpdateSource.Cnb;
        if (privacy == _settings.Current.Privacy && source == _settings.Current.UpdateSource)
        {
            return;
        }

        if (Save("update preferences", s => s with { Privacy = privacy, UpdateSource = source }))
        {
            _updateCancellation?.Cancel();
            _availableUpdate = null;
            _updateStatus.Text = privacy.StrictOffline ? L.T("严格离线已开启，不会检查或下载更新。") : L.T("更新设置已保存。点击“检查更新”可立即检查。");
        }

        ShowUpdateSettings();
    }

    private void UpdateControls()
    {
        var offline = _settings.Current.Privacy.StrictOffline;
        _autoUpdates.IsEnabled = !offline;
        _checkUpdate.IsEnabled = !_updateBusy && !offline;
        _installUpdate.IsEnabled = !_updateBusy && !offline;
        _installUpdate.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        _cancelUpdate.Visibility = _updateBusy ? Visibility.Visible : Visibility.Collapsed;
        _language.IsEnabled = !_updateBusy;
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_updateBusy || _settings.Current.Privacy.StrictOffline || _changingLanguage)
        {
            return;
        }

        _updateBusy = true;
        _updateCancellation = new();
        var token = _updateCancellation.Token;
        var settings = _settings.Current;
        _availableUpdate = null;
        _updateStatus.Text = L.T("正在检查更新…");
        UpdateControls();
        try
        {
            var release = await new UpdateClient(_updateHttp).CheckAsync(settings.UpdateSource, CurrentVersion, UpdateArchitecture, _packaged, settings.Privacy, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            _availableUpdate = release;
            _updateStatus.Text = release is null ? L.T("已是最新版本：{0}", CurrentVersion.ToString(3)) : L.T("发现新版本：{0}", release.Version);
            if (!manual && release is not null)
            {
                _updateNotify(release.Version);
            }
        }
        catch (OperationCanceledException)
        {
            _updateStatus.Text = L.T("更新检查已取消。");
        }
        catch (Exception ex)
        {
            FileLog.Error("update: check failed", ex);
            _updateStatus.Text = L.T("无法检查更新，请稍后重试或切换更新源。");
        }
        finally
        {
            _updateCancellation.Dispose();
            _updateCancellation = null;
            _updateBusy = false;
            UpdateControls();
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_updateBusy || _changingLanguage || _availableUpdate is not { } release || _settings.Current.Privacy.StrictOffline)
        {
            return;
        }

        _updateBusy = true;
        _updateCancellation = new();
        var token = _updateCancellation.Token;
        string? directory = null;
        UpdateControls();
        try
        {
            if (_hasJobs())
            {
                _updateStatus.Text = L.T("请等所有任务完成后再安装更新。");
                return;
            }

            var confirm = new ContentDialog
            {
                XamlRoot = _window.Content.XamlRoot,
                Title = L.T("安装 DropHalo {0}？", release.Version),
                Content = L.T("下载并验证后，DropHalo 会关闭、安装更新并重新打开。你的设置和工具会保留，任务列表会清空。"),
                PrimaryButtonText = L.T("下载并安装"), CloseButtonText = L.T("取消"), DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync().AsTask().ConfigureAwait(true) != ContentDialogResult.Primary)
            {
                return;
            }

            token.ThrowIfCancellationRequested();
            directory = Directory.CreateTempSubdirectory("DropHalo-update-").FullName;
            var installer = Path.Combine(directory, release.FileName);
            var progress = new Progress<double>(fraction => _updateStatus.Text = L.T("正在下载更新：{0}%", (int)(fraction * 100)));
            var client = new UpdateClient(_updateHttp);
            await client.DownloadAsync(release, installer, _settings.Current.Privacy, progress, token).ConfigureAwait(true);
            _updateStatus.Text = L.T("正在验证安装包…");
            var latest = await client.CheckAsync(release.Source, CurrentVersion, UpdateArchitecture, _packaged, _settings.Current.Privacy, token).ConfigureAwait(true);
            if (latest != release)
            {
                throw new InvalidDataException("The release changed or was withdrawn; check again before installing.");
            }

            if (_hasJobs())
            {
                _updateStatus.Text = L.T("请等所有任务完成后再安装更新。");
                return;
            }

            token.ThrowIfCancellationRequested();
            if (!SaveNaming())
            {
                return;
            }

            _checkUpdate.IsEnabled = _installUpdate.IsEnabled = _strictOffline.IsEnabled = _updateSource.IsEnabled = _cancelUpdate.IsEnabled = false;
            await UpdateInstaller.StartAsync(release, installer, UpdateArchitecture, _packaged, UpdateDataDirectory, _hasJobs).ConfigureAwait(true);
            directory = null; // the verified helper owns the installer until Windows finishes with it
            _updateShutdown();
        }
        catch (OperationCanceledException)
        {
            _updateStatus.Text = L.T("更新已取消，当前版本保持不变。");
        }
        catch (Exception ex)
        {
            FileLog.Error("update: download or installation failed", ex);
            _updateStatus.Text = L.T("更新失败，当前版本保持不变。请重试；详情见日志。");
        }
        finally
        {
            if (directory is not null)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            _updateCancellation?.Dispose();
            _updateCancellation = null;
            _updateBusy = false;
            if (!Quitting)
            {
                _strictOffline.IsEnabled = _updateSource.IsEnabled = _cancelUpdate.IsEnabled = true;
                UpdateControls();
            }
        }
    }
}

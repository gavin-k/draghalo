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
    private enum UpdatePhase { Idle, Checking, Confirming, Downloading, Rechecking, VerifyingInstaller, HandedOff }

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
    private UpdatePhase _updatePhase;
    private int _updatePreferencesVersion;
    private bool _showingUpdateSettings;
    private Func<bool> _hasJobs = () => false;
    private Action _updateShutdown = () => { };
    private Action<string> _updateNotify = _ => { };
    private static string UpdateArchitecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    private string UpdateDataDirectory => App.DataDir(Environment.GetCommandLineArgs());
    private static Version CurrentVersion => typeof(App).Assembly.GetName().Version!;
    private bool UpdateBusy => _updatePhase != UpdatePhase.Idle;

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
            _updatePreferencesVersion++;
            _updateCancellation?.Cancel();
            _availableUpdate = null;
            _updateStatus.Text = privacy.StrictOffline ? L.T("严格离线已开启，不会检查或下载更新。") : L.T("更新设置已保存。点击“检查更新”可立即检查。");
        }

        ShowUpdateSettings();
    }

    private void UpdateControls()
    {
        var offline = _settings.Current.Privacy.StrictOffline;
        var handedOff = _updatePhase == UpdatePhase.HandedOff;
        _strictOffline.IsEnabled = _updateSource.IsEnabled = !handedOff;
        _autoUpdates.IsEnabled = !offline && !handedOff;
        _checkUpdate.IsEnabled = _installUpdate.IsEnabled = !UpdateBusy && !offline;
        _installUpdate.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        _cancelUpdate.Visibility = _updatePhase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Rechecking or UpdatePhase.VerifyingInstaller
            ? Visibility.Visible : Visibility.Collapsed;
        _language.IsEnabled = !UpdateBusy;
    }

    private void SetUpdatePhase(UpdatePhase phase)
    {
        _updatePhase = phase;
        if (!Quitting)
        {
            UpdateControls();
        }
    }

    private bool CurrentUpdatePreferences(int version) => !Quitting && version == _updatePreferencesVersion;

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (Quitting || UpdateBusy || _settings.Current.Privacy.StrictOffline || _changingLanguage)
        {
            return;
        }

        using var cancellation = _updateCancellation = new();
        var token = cancellation.Token;
        var preferences = _updatePreferencesVersion;
        var settings = _settings.Current;
        _availableUpdate = null;
        _updateStatus.Text = L.T("正在检查更新…");
        SetUpdatePhase(UpdatePhase.Checking);
        try
        {
            var release = await new UpdateClient(_updateHttp).CheckNewestAsync(settings.UpdateSource, CurrentVersion, UpdateArchitecture, _packaged, settings.Privacy,
                sourceFailed: (source, ex) => FileLog.Error($"update: {source} check failed", ex), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            _availableUpdate = release;
            if (release is { } fallback && fallback.Source != settings.UpdateSource)
            {
                FileLog.Info($"update: {settings.UpdateSource} has nothing newer or failed; offering {fallback.Version} from {fallback.Source}");
                _updateStatus.Text = L.T("发现新版本：{0}（CNB 镜像暂时没有这个版本，这次从 GitHub 下载）", fallback.Version);
            }
            else
            {
                _updateStatus.Text = release is null ? L.T("已是最新版本：{0}", CurrentVersion.ToString(3)) : L.T("发现新版本：{0}", release.Version);
            }
            if (!manual && release is not null)
            {
                _updateNotify(release.Version);
            }
        }
        catch (OperationCanceledException)
        {
            if (CurrentUpdatePreferences(preferences))
            {
                _updateStatus.Text = L.T("更新检查已取消。");
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("update: check failed", ex);
            if (CurrentUpdatePreferences(preferences))
            {
                _updateStatus.Text = L.T("无法检查更新，请稍后重试或切换更新源。");
            }
        }
        finally
        {
            _updateCancellation = null;
            SetUpdatePhase(UpdatePhase.Idle);
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (Quitting || UpdateBusy || _changingLanguage || _availableUpdate is not { } release || _settings.Current.Privacy.StrictOffline)
        {
            return;
        }

        using var cancellation = _updateCancellation = new();
        var token = cancellation.Token;
        var preferences = _updatePreferencesVersion;
        string? directory = null;
        SetUpdatePhase(UpdatePhase.Confirming);
        try
        {
            if (_hasJobs())
            {
                _updateStatus.Text = L.T("请等所有任务完成后再安装更新。");
                return;
            }

            if (_window.Content?.XamlRoot is not { } xamlRoot)
            {
                return;
            }

            var confirm = new ContentDialog
            {
                XamlRoot = xamlRoot,
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
            var progress = new Progress<double>(fraction =>
            {
                if (ReferenceEquals(_updateCancellation, cancellation) && _updatePhase == UpdatePhase.Downloading && !token.IsCancellationRequested)
                {
                    _updateStatus.Text = L.T("正在下载更新：{0}%", (int)(fraction * 100));
                }
            });
            var client = new UpdateClient(_updateHttp);
            SetUpdatePhase(UpdatePhase.Downloading);
            await client.DownloadAsync(release, installer, _settings.Current.Privacy, progress, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            _updateStatus.Text = L.T("正在验证安装包…");
            SetUpdatePhase(UpdatePhase.Rechecking);
            var latest = await client.CheckAsync(release.Source, CurrentVersion, UpdateArchitecture, _packaged, _settings.Current.Privacy, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (latest != release)
            {
                _availableUpdate = null;
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

            SetUpdatePhase(UpdatePhase.VerifyingInstaller);
            if (!await UpdateInstaller.StartAsync(release, installer, UpdateArchitecture, _packaged, UpdateDataDirectory, _hasJobs, () =>
            {
                directory = null; // The approved helper now owns the installer.
                SetUpdatePhase(UpdatePhase.HandedOff);
                _updateShutdown();
            }, token).ConfigureAwait(true) && CurrentUpdatePreferences(preferences))
            {
                _updateStatus.Text = L.T("请等所有任务完成后再安装更新。");
            }
        }
        catch (OperationCanceledException)
        {
            if (CurrentUpdatePreferences(preferences))
            {
                _updateStatus.Text = L.T("更新已取消，当前版本保持不变。");
            }
        }
        catch (Exception ex)
        {
            FileLog.Error("update: download or installation failed", ex);
            if (CurrentUpdatePreferences(preferences))
            {
                _updateStatus.Text = L.T("更新失败，当前版本保持不变。请重试；详情见日志。");
            }
        }
        finally
        {
            if (directory is not null)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            _updateCancellation = null;
            if (_updatePhase != UpdatePhase.HandedOff)
            {
                SetUpdatePhase(UpdatePhase.Idle);
            }
        }
    }
}

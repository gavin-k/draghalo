using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using Windows.Win32.UI.Accessibility;

namespace DropHalo.Acceptance;

internal static partial class AppScenario
{
    public static int RunUpdates(string exe)
    {
        var checks = new Checks();
        if (AnotherInstanceRunning(checks, "update scenario owns the app", "close the running app first"))
        {
            return checks.Report("DropHalo updates");
        }

        var profile = Path.Combine(Path.GetTempPath(), "DropHaloAcceptance", "updates profile 中文");
        Directory.CreateDirectory(profile);
        var settings = Path.Combine(profile, "settings.json");
        File.WriteAllText(settings, """{"version":2,"onboardingDone":true,"language":"English","privacy":{"strictOffline":true,"checkForUpdates":true}}""");
        var log = new AppLog();
        using var app = Process.Start(new ProcessStartInfo(exe, ["--data-dir", profile, "--settings"]) { UseShellExecute = false })!;
        try
        {
            checks.Check("updater app starts", log.WaitFor(30000, "ready:") is not null, "ready log");
            var offline = Element(EnglishSettingsTitle, "strict-offline", out var how, byId: true);
            checks.Check("strict offline disables manual and automatic checks", offline is not null
                && Element(EnglishSettingsTitle, "check-update", out _, byId: true)?.CurrentIsEnabled == false
                && Element(EnglishSettingsTitle, "auto-updates", out _, byId: true)?.CurrentIsEnabled == false, how);
            Thread.Sleep(47000);
            checks.Check("strict offline startup timer makes no network request", log.FindSinceStart("network: ") is null, log.FindSinceStart("network: ") ?? "no network event");
            checks.Check("CNB is the default source", Selected("CNB"), "default source");
            checks.Check("GitHub can be selected and saved", Pick("update-source", "GitHub", out how, EnglishSettingsTitle)
                && Checks.Wait(() => Source() == "GitHub", 3000), how);
            ((IUIAutomationTogglePattern)offline!.GetCurrentPattern(UIA_PATTERN_ID.UIA_TogglePatternId)).Toggle();
            checks.Check("leaving offline enables manual check", Checks.Wait(() => Element(EnglishSettingsTitle, "check-update", out _, byId: true)?.CurrentIsEnabled == true, 3000), "manual button enabled");
            Check("GitHub");
            checks.Check("switching back to CNB is saved", Pick("update-source", "CNB", out how, EnglishSettingsTitle)
                && Checks.Wait(() => Source() == "Cnb", 3000), how);
            Check("CNB");
            using var shot = Capture(SystemInformation.VirtualScreen);
            shot.Save(Path.Combine(profile, "updates-settings.png"), ImageFormat.Png);
            var automatic = Element(EnglishSettingsTitle, "auto-updates", out how, byId: true)!;
            ((IUIAutomationTogglePattern)automatic.GetCurrentPattern(UIA_PATTERN_ID.UIA_TogglePatternId)).Toggle();
            checks.Check("automatic checks can be disabled", Checks.Wait(() => { using var json = JsonDocument.Parse(File.ReadAllText(settings)); return !json.RootElement.GetProperty("privacy").GetProperty("checkForUpdates").GetBoolean(); }, 3000), how);
            checks.Check("app stays running after checks and cancellation", !app.HasExited, "current process kept");

            void Check(string source)
            {
                var invoked = Invoke(EnglishSettingsTitle, "Check for updates", out var detail);
                var complete = invoked && Checks.Wait(() => Element(EnglishSettingsTitle, "check-update", out _, byId: true)?.CurrentIsEnabled == true, 60000);
                var status = Element(EnglishSettingsTitle, "update-status", out _, byId: true)?.CurrentName.ToString() ?? "";
                checks.Check($"{source} checks the real public release", complete && (status.StartsWith("You are up to date", StringComparison.Ordinal) || status.StartsWith("New version available", StringComparison.Ordinal)), status.Length > 0 ? status : detail);
                if (status.StartsWith("New version available", StringComparison.Ordinal))
                {
                    var prompted = Invoke(EnglishSettingsTitle, "Download and install", out detail)
                        && Element(EnglishSettingsTitle, "Cancel", out _) is not null;
                    checks.Check($"{source} asks before downloading or exiting", prompted && !app.HasExited, detail);
                    checks.Check($"{source} install cancellation keeps the current app", prompted && Invoke(EnglishSettingsTitle, "Cancel", out detail) && !app.HasExited, detail);
                }
            }

            string? Source() { using var json = JsonDocument.Parse(File.ReadAllText(settings)); return json.RootElement.GetProperty("updateSource").GetString(); }
            bool Selected(string name)
            {
                var combo = Element(EnglishSettingsTitle, "update-source", out _, byId: true);
                return combo is not null && ((IUIAutomationSelectionPattern)combo.GetCurrentPattern(UIA_PATTERN_ID.UIA_SelectionPatternId)).GetCurrentSelection().GetElement(0).CurrentName.ToString() == name;
            }
        }
        catch (Exception ex) { checks.Check("update UI scenario", false, ex.ToString()); }
        finally
        {
            using var quit = Process.Start(new ProcessStartInfo(exe, ["--quit"]) { UseShellExecute = false });
            quit?.WaitForExit(10000);
            if (!app.WaitForExit(10000)) { app.Kill(); }
        }

        return checks.Report("DropHalo updates");
    }
}

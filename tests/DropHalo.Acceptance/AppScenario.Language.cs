using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;
using DropHalo.Core.Localization;
using DropHalo.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace DropHalo.Acceptance;

internal static partial class AppScenario
{
    /// <summary>The task window's title in English; it must match Strings.en.json in DropHalo.App.</summary>
    private const string EnglishQueueTitle = "DropHalo Tasks";
    private const string EnglishSettingsTitle = "DropHalo Settings";

    public static int RunLanguage(string exe)
    {
        var checks = new Checks();
        if (!AnotherInstanceRunning(checks, "language scenario owns the app", "another DropHalo is running; close it first"))
        {
            Attempt(checks, "system language at sign-in", () => AtSignIn(checks, exe));
            Attempt(checks, "language confirmation and restart", () => InEnglish(checks, exe));
        }

        return checks.Report("DropHalo language confirmation and restart");
    }

    /// <summary>
    /// With English picked in settings.json, DropHalo speaks English from its start even on a Chinese Windows:
    /// the log says so, and launching it again shows the task window under its English title, with a Settings
    /// button. Canceling a change keeps the current process and settings; a failed save does too. Confirming
    /// restarts through all supported languages on the same profile with the settings window reopened.
    /// </summary>
    private static void InEnglish(Checks checks, string exe)
    {
        var profile = Path.Combine(Path.GetTempPath(), "DropHaloAcceptance", "language profile 中文") + Path.DirectorySeparatorChar;
        if (Directory.Exists(profile))
        {
            Directory.Delete(profile, recursive: true);
        }

        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), """{ "version": 2, "onboardingDone": true, "language": "English" }""");
        var log = new AppLog();
        using var app = StartApp(exe, profile);
        try
        {
            var ready = log.WaitFor(30000, "ready:");
            var language = log.FindSinceStart("language: ");
            if (!checks.Check("with English picked, DropHalo starts in English", ready is not null && language?.Contains("language: English (setting English, ", StringComparison.Ordinal) == true,
                $"{(ready is null ? "no 'ready:' line within 30 s" : language ?? "no 'language:' line")}"))
            {
                return;
            }

            // Launching it again shows the task window, as the tray icon would.
            using (var again = Process.Start(new ProcessStartInfo(exe, ["--data-dir", profile]) { UseShellExecute = false }))
            {
                again?.WaitForExit(10000);
            }

            var window = default(HWND);
            var shown = Checks.Wait(() => !(window = PInvoke.FindWindow(null, EnglishQueueTitle)).IsNull && PInvoke.IsWindowVisible(window), 5000);
            var how = $"no visible '{EnglishQueueTitle}'";
            var settings = shown ? Element(EnglishQueueTitle, "Settings", out how) : null;
            checks.Check("its task window is titled and labelled in English", shown && settings is not null, how);

            if (!Invoke(EnglishQueueTitle, "Settings", out how))
            {
                checks.Check("English settings opens", false, how);
                return;
            }

            var file = Path.Combine(profile, "settings.json");
            var before = File.ReadAllText(file);
            var prompted = Pick("language", "简体中文", out how, EnglishSettingsTitle)
                && Element(EnglishSettingsTitle, "Restart to change language?", out how) is not null;
            checks.Check("picking a language asks before saving or exiting", prompted && File.ReadAllText(file) == before && !app.HasExited, how);
            var canceled = prompted && Invoke(EnglishSettingsTitle, "Cancel", out how);
            checks.Check("canceling keeps the original language and process",
                canceled && Checks.Wait(() => LanguageSelected(EnglishSettingsTitle, "English"), 3000)
                && File.ReadAllText(file) == before && !app.HasExited, how);

            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            try
            {
                log.Mark();
                var confirmed = Pick("language", "简体中文", out how, EnglishSettingsTitle)
                    && Invoke(EnglishSettingsTitle, "Restart and apply", out how);
                var failed = confirmed ? log.WaitFor(3000, "settings: could not save language Chinese") : null;
                checks.Check("a failed save keeps the original language and does not restart",
                    failed is not null && Checks.Wait(() => LanguageSelected(EnglishSettingsTitle, "English"), 3000)
                    && File.ReadAllText(file) == before && !app.HasExited, failed ?? how);
            }
            finally
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }

            Switch(EnglishSettingsTitle, "简体中文", "Restart and apply", "Chinese", SettingsTitle, "唤出轮盘");
            checks.Check("confirming exits the original process", app.WaitForExit(10000), $"exited={app.HasExited}");
            Switch(SettingsTitle, "English", "重启并应用", "English", EnglishSettingsTitle, "Opening the wheels");

            var title = EnglishSettingsTitle;
            var apply = "Restart and apply";
            foreach (var (option, setting, nextTitle, label, nextApply, replace) in new[]
            {
                ("한국어", "Korean", "DropHalo 설정", "휠 열기", "다시 시작하고 적용", "교체"),
                ("日本語", "Japanese", "DropHalo 設定", "ホイールを開く", "再起動して適用", "置き換える"),
                ("Deutsch", "German", "DropHalo Einstellungen", "Räder öffnen", "Neu starten und anwenden", "Ersetzen"),
                ("Français", "French", "DropHalo Paramètres", "Ouvrir les roues", "Redémarrer et appliquer", "Remplacer"),
                ("Español", "Spanish", "DropHalo Ajustes", "Abrir las ruedas", "Reiniciar y aplicar", "Reemplazar"),
            })
            {
                Switch(title, option, apply, setting, nextTitle, label);
                using var shot = Capture(SystemInformation.VirtualScreen);
                shot.Save(Path.Combine(profile, $"settings-{setting}.png"), ImageFormat.Png);
                var shell = Loc.Catalog(typeof(ShellFileOperations).Assembly, Enum.Parse<UiLanguage>(setting));
                checks.Check($"{setting} conflict dialog translations are embedded", shell.GetValueOrDefault("替换") == replace, replace);
                if (setting is "German" or "French" or "Spanish")
                {
                    var wheelTitle = setting == "German" ? "DropHalo Rad" : setting == "French" ? "DropHalo Roue" : "DropHalo Rueda";
                    var toolLabel = setting == "German" ? "Bilder komprimieren" : setting == "French" ? "Compresser les images" : "Comprimir imágenes";
                    var jpg = Path.Combine(profile, $"wheel-{setting}.jpg");
                    WriteJpeg(jpg, 640, 400);
                    using var source = DragSource.Open(jpg, explorer: false);
                    var mid = source.PressAndDrag();
                    log.Mark();
                    try
                    {
                        if (Tap(checks, log, mid, CtrlAlt) is not null)
                        {
                            log.WaitFor(2000, "file(s) entered");
                            checks.Check($"{setting} engine labels appear on the tools wheel", Element(wheelTitle, toolLabel, out var detail) is not null, detail);
                            using var wheelShot = Capture(SystemInformation.VirtualScreen);
                            wheelShot.Save(Path.Combine(profile, $"wheel-{setting}.png"), ImageFormat.Png);
                        }
                    }
                    finally
                    {
                        PressAndRelease(VIRTUAL_KEY.VK_ESCAPE);
                        Input.LeftUp();
                    }
                }
                title = nextTitle;
                apply = nextApply;
            }

            Switch(title, "English", apply, "English", EnglishSettingsTitle, "Opening the wheels");

            void Switch(string title, string option, string confirm, string languageName, string nextTitle, string label)
            {
                log.Mark();
                var confirmed = Pick("language", option, out var detail, title) && Invoke(title, confirm, out detail);
                var readyAgain = confirmed ? log.WaitFor(30000, "ready:") : null;
                var picked = log.Find($"language: {languageName} (setting {languageName}, ");
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                var savedLanguage = json.RootElement.GetProperty("language").GetString();
                var error = log.Find(" ERROR ");
                checks.Check($"confirming restarts in {languageName} and reopens settings on the same profile",
                    readyAgain is not null && picked is not null && savedLanguage == languageName && error is null
                    && Element(nextTitle, label, out _) is not null && LanguageSelected(nextTitle, option),
                    $"{detail}; {picked ?? "no language line"}; saved {savedLanguage}; error {error ?? "none"}");
                checks.Check("the replacement starts once without handing activation back to the old instance",
                    log.Count("ready:") == 1 && log.Find("another DropHalo is already running") is null,
                    $"ready count={log.Count("ready:")}");
            }
        }
        finally
        {
            using (var quit = Process.Start(new ProcessStartInfo(exe, ["--quit"]) { UseShellExecute = false }))
            {
                quit?.WaitForExit(10000);
            }

            if (!app.WaitForExit(10000))
            {
                app.Kill();
            }

            var error = log.Find(" ERROR ");
            checks.Check("language restart logged no errors", error is null, error ?? "none");
            checks.Check("the restarted app exits on --quit",
                Checks.Wait(() => PInvoke.FindWindow(null, EnglishSettingsTitle).IsNull && PInvoke.FindWindow(null, SettingsTitle).IsNull, 10000),
                "settings windows closed");
        }
    }

    private static bool LanguageSelected(string title, string label)
    {
        if (Element(title, "language", out _, byId: true) is not { } combo)
        {
            return false;
        }

        var selected = ((IUIAutomationSelectionPattern)combo.GetCurrentPattern(UIA_PATTERN_ID.UIA_SelectionPatternId)).GetCurrentSelection();
        return selected.Length == 1 && selected.GetElement(0).CurrentName.ToString() == label;
    }
}

using DropHalo.Core.Actions;
using DropHalo.Core.Jobs;
using DropHalo.Core.Settings;
using DropHalo.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DropHalo.Acceptance;

internal static class DialogsScenario
{
    public static int Run()
    {
        var checks = new Checks();
        var folder = Directory.CreateTempSubdirectory("drophalo-dialogs-");
        try
        {
            var prompt = new FolderPrompt(folder.Name, "Choose", folder.FullName, Guid.NewGuid());
            var target = Path.Combine(folder.FullName, "existing.txt");
            File.WriteAllText(target, "fixture");
            Verify(checks, ConflictDialog.Title, token => ConflictDialog.AskAsync(target, false, token), answer => answer.Policy == ConflictPolicy.Skip);
            Verify(checks, prompt.Title, token => FolderDialog.PickAsync(prompt, token), answer => answer is null);
        }
        finally
        {
            folder.Delete(recursive: true);
        }

        return checks.Report("Native dialog cancellation");
    }

    private static void Verify<T>(Checks checks, string title, Func<CancellationToken, Task<T>> show, Func<T, bool> closed)
    {
        const int attempts = 24;
        var canceled = 0;
        for (var i = 0; i < attempts; i++)
        {
            using var cancel = new CancellationTokenSource();
            var answer = show(cancel.Token);
            Thread.Sleep(i % 12);
            cancel.Cancel();
            if (Settled(checks, title, answer) && answer.IsCanceled)
            {
                canceled++;
            }
        }

        checks.Check($"{title}: cancel around creation", canceled == attempts, $"{canceled}/{attempts} completed as canceled");

        using var visibleCancel = new CancellationTokenSource();
        var visible = show(visibleCancel.Token);
        var opened = Checks.Wait(() => !Window(title).IsNull, 3000);
        visibleCancel.Cancel();
        checks.Check($"{title}: cancel while visible", Settled(checks, title, visible) && opened && visible.IsCanceled, "the token closes the dialog");

        using var userCancel = new CancellationTokenSource();
        var userClosed = show(userCancel.Token);
        opened = Checks.Wait(() => !Window(title).IsNull, 3000);
        Close(title);
        checks.Check($"{title}: normal close", Settled(checks, title, userClosed) && opened && userClosed.IsCompletedSuccessfully && closed(userClosed.Result),
            "closing without a token cancellation returns Skip or an empty selection");
    }

    private static bool Settled(Checks checks, string title, Task answer)
    {
        if (Checks.Wait(() => answer.IsCompleted, 3000))
        {
            if (answer.IsFaulted)
            {
                checks.Check($"{title}: dialog completed without error", false, answer.Exception!.GetBaseException().Message);
            }

            return true;
        }

        Close(title);
        Checks.Wait(() => answer.IsCompleted, 3000);
        checks.Check($"{title}: dialog settled by the deadline", false, "needed a manual close");
        return false;
    }

    private static void Close(string title)
    {
        var window = Window(title);
        if (!window.IsNull)
        {
            PInvoke.PostMessage(window, PInvoke.WM_CLOSE, default, default);
        }
    }

    private static unsafe HWND Window(string title)
    {
        uint pid = 0;
        var window = PInvoke.FindWindow("#32770", title);
        return !window.IsNull && PInvoke.IsWindowVisible(window) && PInvoke.GetWindowThreadProcessId(window, &pid) != 0 && pid == Environment.ProcessId
            ? window : HWND.Null;
    }
}

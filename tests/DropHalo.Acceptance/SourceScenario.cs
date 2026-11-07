using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace DropHalo.Acceptance;

internal static class SourceScenario
{
    public static int Run()
    {
        var checks = new Checks();
        if (!checks.Check("source guard starts with the mouse released", !LeftIsDown(), "The test desktop must be idle."))
        {
            return checks.Report("Source guard");
        }

        var dir = Path.Combine(Path.GetTempPath(), "DropHaloAcceptance", "source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "source.txt");
        File.WriteAllText(file, "source guard");
        try
        {
            using var source = DragSource.Open(file, explorer: true);
            using var blocker = new Form
            {
                Text = "Acceptance source blocker",
                Bounds = new Rectangle(source.Grab.X - 120, source.Grab.Y - 110, 240, 220),
                StartPosition = FormStartPosition.Manual,
                TopMost = true,
            };
            var panel = new Panel { Dock = DockStyle.Fill };
            var presses = 0;
            panel.MouseDown += (_, _) => presses++;
            blocker.Controls.Add(panel);
            blocker.Show();
            blocker.Activate();
            Application.DoEvents();
            if (!checks.Check("the test blocker covers the source point",
                PInvoke.GetAncestor(PInvoke.WindowFromPoint(source.Grab), GET_ANCESTOR_FLAGS.GA_ROOT) == new HWND(blocker.Handle), "owned blocker is topmost"))
            {
                return checks.Report("Source guard");
            }

            var rejected = false;
            try
            {
                source.PressAndDrag();
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Explorer source", StringComparison.Ordinal))
            {
                rejected = true;
                Log.Info(ex.Message);
            }
            finally
            {
                Application.DoEvents();
                checks.Check("an obscured Explorer source is rejected before input", rejected, $"blocker received {presses} mouse presses");
                checks.Check("source rejection never presses the mouse", !LeftIsDown() && presses == 0, $"button down={LeftIsDown()}, blocker presses={presses}");
                Input.LeftUp();
                blocker.Close();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        return checks.Report("Source guard");
    }

    private static bool LeftIsDown() => (PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_LBUTTON) & 0x8000) != 0;
}

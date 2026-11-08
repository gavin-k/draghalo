using System.Text;

namespace DropHalo.Acceptance;

internal static class Program
{
    private const string Usage = """
        DropHalo.Acceptance: drives the M0 flow with real mouse and keyboard input.

          DropHalo.Acceptance host [--explorer]   DropTargetHost alone: drop on a sector, off the wheel,
                                                   on the centre, on a disabled sector
          DropHalo.Acceptance hooks [--explorer]  DragMonitor: drag phases, Shift / Ctrl+Alt / Win taps, long hold
          DropHalo.Acceptance fileops             ShellFileOperations: recycle, move with a collision
          DropHalo.Acceptance source              verifies that an obscured Explorer source is never pressed
          DropHalo.Acceptance app --exe <DropHalo.exe> [--root <folder>] [--explorer]
                                                   the M0 flow against the real app: JPG to PNG, Undo,
                                                   then a release off the wheel. --root is the package's
                                                   install folder when --exe is the MSIX execution alias
          DropHalo.Acceptance performance --exe <DropHalo.exe> [--iterations 30] [--reopen-explorer] [--grouped]
                                                   repeated real Explorer drags; every first frame must meet 150 ms
                                                   --grouped opens Tools with a real group and ring-sized footprint
          DropHalo.Acceptance archives --exe <DropHalo.exe> [--iterations 30]
                                                   repeated subtitle and ZIP conversions through real Explorer drags
          DropHalo.Acceptance language --exe <DropHalo.exe>
                                                   language confirmation, cancel, save failure and automatic restart
          DropHalo.Acceptance updates --exe <DropHalo.exe>
                                                   offline startup, source persistence, real checks and install cancellation
          DropHalo.Acceptance dock --exe <DropHalo.exe> [--iterations 10]
                                                   multi-file Explorer drops, 全部拖出 cancellation and copying
          DropHalo.Acceptance --dragsource <file> --at X,Y   (internal) WinForms drag source
          DropHalo.Acceptance --locate <file>                (internal) hook-free Explorer item locator
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Console.OutputEncoding = new UTF8Encoding(false);

        if (ArgValue(args, "--dragsource") is { } file)
        {
            var at = (ArgValue(args, "--at") ?? "100,100").Split(',');
            Application.Run(new DragSourceForm(file, new Point(int.Parse(at[0]), int.Parse(at[1]))));
            return 0;
        }

        if (ArgValue(args, "--locate") is { } locate)
        {
            Log.Open("locate");
            return ExplorerSource.LocateMain(locate);
        }

        var explorer = args.Contains("--explorer");
        switch (args.FirstOrDefault())
        {
            case "host":
                Log.Open("host");
                return HostScenario.Run(explorer);
            case "hooks":
                Log.Open("hooks");
                return HooksScenario.Run(explorer);
            case "fileops":
                Log.Open("fileops");
                return FileOpsScenario.Run();
            case "source":
                Log.Open("source");
                return SourceScenario.Run();
            case "app" when ArgValue(args, "--exe") is { } exe:
                Log.Open("app");
                return AppScenario.Run(Path.GetFullPath(exe), explorer, ArgValue(args, "--root"));
            case "language" when ArgValue(args, "--exe") is { } languageExe:
                Log.Open("language");
                return AppScenario.RunLanguage(Path.GetFullPath(languageExe));
            case "updates" when ArgValue(args, "--exe") is { } updateExe:
                Log.Open("updates");
                return AppScenario.RunUpdates(Path.GetFullPath(updateExe));
            case "dock" when ArgValue(args, "--exe") is { } dockExe:
                if (!int.TryParse(ArgValue(args, "--iterations") ?? "10", out var dockIterations) || dockIterations < 1)
                {
                    Console.Error.WriteLine("--iterations must be a positive integer.");
                    return 2;
                }

                Log.Open("dock");
                return AppScenario.RunDockBatch(Path.GetFullPath(dockExe), dockIterations);
            case "performance" when ArgValue(args, "--exe") is { } performanceExe:
                if (!int.TryParse(ArgValue(args, "--iterations") ?? "30", out var iterations) || iterations < 1)
                {
                    Console.Error.WriteLine("--iterations must be a positive integer.");
                    return 2;
                }

                Log.Open("performance");
                return AppScenario.RunPerformance(Path.GetFullPath(performanceExe), iterations, args.Contains("--reopen-explorer"), args.Contains("--grouped"));
            case "archives" when ArgValue(args, "--exe") is { } archiveExe:
                if (!int.TryParse(ArgValue(args, "--iterations") ?? "30", out var archiveIterations) || archiveIterations < 1)
                {
                    Console.Error.WriteLine("--iterations must be a positive integer.");
                    return 2;
                }

                Log.Open("archives");
                return AppScenario.RunArchives(Path.GetFullPath(archiveExe), archiveIterations);
            default:
                Console.WriteLine(Usage);
                return 2;
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

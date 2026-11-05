using DropHalo.Core.Jobs;
using DropHalo.Shell;

namespace DropHalo.Acceptance;

/// <summary><see cref="ShellFileOperations"/> against the real Shell: recycle, and move with a name collision.</summary>
internal static class FileOpsScenario
{
    public static int Run()
    {
        var checks = new Checks();
        var ops = new ShellFileOperations();
        var dir = Path.Combine(Path.GetTempPath(), "DropHaloAcceptance", "fileops");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("HHmmss");

        try
        {
            var a = Write(Path.Combine(dir, $"recycle-{stamp}-a.png"));
            var b = Write(Path.Combine(dir, $"recycle-{stamp}-b.png"));
            var t0 = Log.NowMs;
            ops.RecycleAsync([a, b, Path.Combine(dir, "already-gone.png")]).GetAwaiter().GetResult();
            checks.Check("recycled files are gone from the folder", !File.Exists(a) && !File.Exists(b), $"{Log.NowMs - t0:F0} ms");
            var binned = InRecycleBin(dir);
            checks.Check("recycled files are in the Recycle Bin", binned.Contains(Path.GetFileNameWithoutExtension(a)) && binned.Contains(Path.GetFileNameWithoutExtension(b)),
                $"from {dir}: [{string.Join(", ", binned)}]");

            var source = Write(Path.Combine(dir, $"move-{stamp}.png"), 7);
            var dest = Write(Path.Combine(dir, "moved", $"move-{stamp}.png"), 3);
            var moved = ops.MoveAsync([new FileMove(source, dest)]).GetAwaiter().GetResult();
            var landed = Directory.GetFiles(Path.GetDirectoryName(dest)!, $"move-{stamp}*.png");
            checks.Check("move with a collision keeps both files", !File.Exists(source) && new FileInfo(dest).Length == 3 && landed.Length == 2,
                string.Join(", ", landed.Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length} B)")));
            checks.Check("move reports the actual numbered destination", moved.Count == 1 && moved[0].From == source
                && moved[0].To != dest && File.Exists(moved[0].To) && new FileInfo(moved[0].To).Length == 7,
                string.Join(", ", moved));

            var copySource = Write(Path.Combine(dir, $"copy-{stamp}.png"), 11);
            var copyDest = Write(Path.Combine(dir, "copied", $"copy-{stamp}.png"), 5);
            var copied = ops.CopyAsync([new FileMove(copySource, copyDest)]).GetAwaiter().GetResult();
            checks.Check("copy reports the actual numbered destination", copied.Count == 1 && copied[0].From == copySource
                && copied[0].To != copyDest && File.Exists(copied[0].To) && new FileInfo(copied[0].To).Length == 11,
                string.Join(", ", copied));
            checks.Check("copy keeps the source and the conflicting file", File.Exists(copySource) && new FileInfo(copyDest).Length == 5, copyDest);

            var folderSource = Path.Combine(dir, $"tree-{stamp}");
            Write(Path.Combine(folderSource, "child", "a.txt"));
            var folderDest = Path.Combine(dir, "folder-copies", Path.GetFileName(folderSource));
            var folders = ops.CopyAsync([new FileMove(folderSource, folderDest)]).GetAwaiter().GetResult();
            checks.Check("folder copy reports only the selected root", folders.Count == 1
                && folders[0].From == folderSource && string.Equals(folders[0].To, folderDest, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(folderDest, "child", "a.txt")), string.Join(", ", folders));
        }
        catch (Exception ex)
        {
            checks.Check("no exception", false, ex.ToString());
        }

        return checks.Report("ShellFileOperations");
    }

    private static string Write(string path, int bytes = 16)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>
    /// Names, without extension, of Recycle Bin items deleted from <paramref name="folder"/>, through
    /// Shell.Application. Display names drop the extension when Explorer hides known ones.
    /// </summary>
    public static HashSet<string> InRecycleBin(string folder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic bin = shell.NameSpace(10);
        foreach (dynamic item in bin.Items())
        {
            string from = item.ExtendedProperty("System.Recycle.DeletedFrom") ?? "";
            if (string.Equals(from.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                names.Add(Path.GetFileNameWithoutExtension((string)item.Name));
            }
        }

        return names;
    }
}

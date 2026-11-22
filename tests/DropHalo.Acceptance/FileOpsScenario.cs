using DropHalo.Core.Jobs;
using DropHalo.Shell;

namespace DropHalo.Acceptance;

/// <summary>
/// <see cref="ShellFileOperations"/> against the real Shell: recycle, move and copy with a name collision, a folder
/// copy, and a move and a copy canceled part-way.
/// </summary>
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

            CanceledPartWay(checks, ops, dir, stamp, copy: false);
            CanceledPartWay(checks, ops, dir, stamp, copy: true);
        }
        catch (Exception ex)
        {
            checks.Check("no exception", false, ex.ToString());
        }

        return checks.Report("ShellFileOperations");
    }

    /// <summary>
    /// Cancels a transfer of many small files as soon as the first one shows up in the destination. Only a run the
    /// Shell stopped between its first and its last item says anything, so one that moved all or none is tried again
    /// with more files, and never counts as a pass.
    /// </summary>
    private static void CanceledPartWay(Checks checks, ShellFileOperations ops, string dir, string stamp, bool copy)
    {
        var verb = copy ? "copy" : "move";
        var tried = new List<string>();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var count = 400 * attempt;
            var from = Path.Combine(dir, $"cancel-{verb}-{stamp}-{attempt}");
            var to = from + "-to";
            Directory.CreateDirectory(to);
            var plan = Enumerable.Range(0, count).Select(i => new FileMove(Write(Path.Combine(from, $"{i:D4}.bin")), Path.Combine(to, $"{i:D4}.bin"))).ToList();
            using var cancel = new CancellationTokenSource();
            var watcher = Task.Run(() =>
            {
                while (!cancel.IsCancellationRequested && !Directory.EnumerateFiles(to).Any())
                {
                    Thread.Sleep(0);
                }

                cancel.Cancel();
            });

            IReadOnlyList<FileMove>? reported = null;
            string outcome;
            try
            {
                var all = (copy ? ops.CopyAsync(plan, cancel.Token) : ops.MoveAsync(plan, cancel.Token)).GetAwaiter().GetResult();
                outcome = $"finished all {all.Count}";
            }
            catch (PartialTransferException ex)
            {
                reported = ex.Transferred;
                outcome = $"reported {reported.Count}";
            }
            catch (OperationCanceledException ex)
            {
                outcome = $"canceled without a report ({ex.GetType().Name})";
            }
            finally
            {
                cancel.Cancel();
                watcher.Wait();
            }

            var arrived = plan.Where(m => File.Exists(m.To)).ToList();
            tried.Add($"attempt {attempt}: {arrived.Count} of {count} arrived, {outcome}");
            if (arrived.Count == 0 || arrived.Count == count)
            {
                continue;
            }

            var named = (reported ?? []).Select(m => (m.From, m.To)).ToHashSet();
            var same = named.Count == arrived.Count && arrived.All(m => named.Any(n =>
                string.Equals(n.From, m.From, StringComparison.OrdinalIgnoreCase) && string.Equals(n.To, m.To, StringComparison.OrdinalIgnoreCase)));
            checks.Check($"a {verb} canceled part-way reports exactly what arrived", reported is not null && same, string.Join("; ", tried));
            var whole = plan.All(m => File.Exists(m.To)
                ? new FileInfo(m.To).Length == 16 && File.Exists(m.From) == copy
                : File.Exists(m.From) && new FileInfo(m.From).Length == 16);
            checks.Check($"a {verb} canceled part-way leaves every item whole, in one place{(copy ? " or both" : "")}", whole,
                $"{arrived.Count} in the destination, {plan.Count(m => File.Exists(m.From))} in the source");
            return;
        }

        checks.Check($"a {verb} canceled part-way reports exactly what arrived", false, "never stopped between the first and the last item: " + string.Join("; ", tried));
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

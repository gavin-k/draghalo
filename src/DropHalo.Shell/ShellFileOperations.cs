using System.Runtime.InteropServices;
using DropHalo.Core.Jobs;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace DropHalo.Shell;

/// <summary>
/// <see cref="IFileOperations"/> through IFileOperation, so undo shows up in the Recycle Bin and in
/// Explorer's own undo history. Each call runs on a fresh STA thread: IFileOperation is apartment-bound
/// and may show Shell UI (the "can't recycle, delete permanently?" prompt).
/// </summary>
public sealed class ShellFileOperations : IFileOperations
{
    public Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0)
        {
            return Task.CompletedTask;
        }

        // FOF_WANTNUKEWARNING overrides FOF_NOCONFIRMATION for files that would be deleted outright
        // (too big for the bin, network share), so nothing is ever destroyed without the user saying so.
        const FILEOPERATION_FLAGS flags = FILEOPERATION_FLAGS.FOF_ALLOWUNDO | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION
            | FILEOPERATION_FLAGS.FOF_SILENT | FILEOPERATION_FLAGS.FOF_NOERRORUI | FILEOPERATION_FLAGS.FOF_WANTNUKEWARNING
            | FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE;
        return RunAsync(flags, (op, release) =>
        {
            foreach (var path in existing)
            {
                op.DeleteItem(release(Item(path)), null);
            }
        }, $"recycle {existing.Count} item(s)", cancellationToken);
    }

    public Task<IReadOnlyList<FileMove>> MoveAsync(IReadOnlyList<FileMove> moves, CancellationToken cancellationToken = default) =>
        TransferAsync(moves, copy: false, cancellationToken);

    public Task<IReadOnlyList<FileMove>> CopyAsync(IReadOnlyList<FileMove> copies, CancellationToken cancellationToken = default) =>
        TransferAsync(copies, copy: true, cancellationToken);

    private static async Task<IReadOnlyList<FileMove>> TransferAsync(IReadOnlyList<FileMove> items, bool copy, CancellationToken cancellationToken)
    {
        // No FOF_NOCONFIRMATION here: it would answer "replace" for a file now sitting at the destination.
        const FILEOPERATION_FLAGS flags = FILEOPERATION_FLAGS.FOF_ALLOWUNDO | FILEOPERATION_FLAGS.FOF_NOCONFIRMMKDIR
            | FILEOPERATION_FLAGS.FOF_RENAMEONCOLLISION | FILEOPERATION_FLAGS.FOF_SILENT;
        var transferred = new List<FileMove>();
        var run = RunAsync(flags, (op, release) =>
        {
            foreach (var item in items)
            {
                var fullTo = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.To));
                var folder = Path.GetDirectoryName(fullTo) ?? throw new ArgumentException($"No folder in {item.To}.", nameof(items));
                Directory.CreateDirectory(folder);
                var progress = new TransferProgress(Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.From)), transferred, cancellationToken);
                if (copy)
                {
                    op.CopyItem(release(Item(item.From)), release(Item(folder)), Path.GetFileName(fullTo), progress);
                }
                else
                {
                    op.MoveItem(release(Item(item.From)), release(Item(folder)), Path.GetFileName(fullTo), progress);
                }
            }
        }, $"{(copy ? "copy" : "move")} {items.Count} item(s)", cancellationToken);
        try
        {
            await run.ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested && transferred.Count > 0)
        {
            // However the Shell stopped, the items it had finished are where they are now: name them.
            throw new PartialTransferException([.. transferred], cancellationToken);
        }

        return transferred;
    }

    private static Task RunAsync(FILEOPERATION_FLAGS flags, Action<IFileOperation, Func<IShellItem, IShellItem>> queue, string what, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var createdItems = new List<IShellItem>();
            IShellItem Track(IShellItem item)
            {
                createdItems.Add(item);
                return item;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                PInvoke.CoCreateInstance(typeof(FileOperation).GUID, null, CLSCTX.CLSCTX_ALL, out IFileOperation op).ThrowOnFailure();
                try
                {
                    op.SetOperationFlags(flags);
                    var cancelSink = new CancellationSink(cancellationToken);
                    op.Advise(cancelSink, out var cookie);
                    try
                    {
                        queue(op, Track);
                        op.PerformOperations();
                    }
                    catch (COMException ex) when (ex.HResult == CancellationSink.UserCancelled && cancellationToken.IsCancellationRequested)
                    {
                        done.SetCanceled(cancellationToken);
                        return;
                    }
                    finally
                    {
                        op.Unadvise(cookie);
                    }

                    op.GetAnyOperationsAborted(out var aborted);
                    if (aborted)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            done.SetCanceled(cancellationToken);
                            return;
                        }

                        throw new IOException($"The Shell did not finish: {what}.");
                    }

                    done.SetResult();
                }
                finally
                {
                    foreach (var shellItem in createdItems)
                    {
                        Marshal.ReleaseComObject(shellItem);
                    }

                    Marshal.ReleaseComObject(op);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                done.SetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "ShellFileOperations" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static IShellItem Item(string path)
    {
        PInvoke.SHCreateItemFromParsingName(Path.GetFullPath(path), null, typeof(IShellItem).GUID, out var item).ThrowOnFailure();
        return (IShellItem)item;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class CancellationSink(CancellationToken cancellationToken) : IFileOperationProgressSink
    {
        public const int UserCancelled = unchecked((int)0x80270000); // COPYENGINE_E_USER_CANCELLED

        private void CheckCancel()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Marshal.ThrowExceptionForHR(UserCancelled);
            }
        }

        public void StartOperations() => CheckCancel();
        public void FinishOperations(HRESULT result) { }
        public void PreRenameItem(uint flags, IShellItem item, PCWSTR name) => CheckCancel();
        public void PostRenameItem(uint flags, IShellItem item, PCWSTR name, HRESULT result, IShellItem created) { }
        public void PreMoveItem(uint flags, IShellItem item, IShellItem folder, PCWSTR name) => CheckCancel();
        public void PostMoveItem(uint flags, IShellItem item, IShellItem folder, PCWSTR name, HRESULT result, IShellItem created) { }
        public void PreCopyItem(uint flags, IShellItem item, IShellItem folder, PCWSTR name) => CheckCancel();
        public void PostCopyItem(uint flags, IShellItem item, IShellItem folder, PCWSTR name, HRESULT result, IShellItem created) { }
        public void PreDeleteItem(uint flags, IShellItem item) => CheckCancel();
        public void PostDeleteItem(uint flags, IShellItem item, HRESULT result, IShellItem created) { }
        public void PreNewItem(uint flags, IShellItem folder, PCWSTR name) => CheckCancel();
        public void PostNewItem(uint flags, IShellItem folder, PCWSTR name, PCWSTR template, uint attributes, HRESULT result, IShellItem created) { }
        public void UpdateProgress(uint total, uint completed) => CheckCancel();
        public void ResetTimer() { }
        public void PauseTimer() { }
        public void ResumeTimer() { }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class TransferProgress(string source, List<FileMove> transferred, CancellationToken cancellationToken) : IFileOperationProgressSink
    {
        private void CheckCancel()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Marshal.ThrowExceptionForHR(CancellationSink.UserCancelled);
            }
        }

        public void PostMoveItem(uint flags, IShellItem item, IShellItem folder, PCWSTR name, HRESULT result, IShellItem created) => Record(item, result, created);

        public void PostCopyItem(uint flags, IShellItem item, IShellItem folder, PCWSTR name, HRESULT result, IShellItem created) => Record(item, result, created);

        private void Record(IShellItem item, HRESULT result, IShellItem? created)
        {
            result.ThrowOnFailure();
            // Folder operations can report children too; only the selected root belongs in the undo plan.
            if (created is not null && string.Equals(Path.TrimEndingDirectorySeparator(PathOf(item)), source, StringComparison.OrdinalIgnoreCase))
            {
                transferred.Add(new(source, Path.TrimEndingDirectorySeparator(PathOf(created))));
            }
        }

        private static unsafe string PathOf(IShellItem item)
        {
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var name);
            try
            {
                return name.ToString();
            }
            finally
            {
                Marshal.FreeCoTaskMem((nint)name.Value);
            }
        }

        public void StartOperations() => CheckCancel();
        public void FinishOperations(HRESULT result) { }
        public void PreRenameItem(uint flags, IShellItem item, PCWSTR name) => CheckCancel();
        public void PostRenameItem(uint flags, IShellItem item, PCWSTR name, HRESULT result, IShellItem created) { }
        public void PreMoveItem(uint flags, IShellItem item, IShellItem folder, PCWSTR name) => CheckCancel();
        public void PreCopyItem(uint flags, IShellItem item, IShellItem folder, PCWSTR name) => CheckCancel();
        public void PreDeleteItem(uint flags, IShellItem item) => CheckCancel();
        public void PostDeleteItem(uint flags, IShellItem item, HRESULT result, IShellItem created) { }
        public void PreNewItem(uint flags, IShellItem folder, PCWSTR name) => CheckCancel();
        public void PostNewItem(uint flags, IShellItem folder, PCWSTR name, PCWSTR template, uint attributes, HRESULT result, IShellItem created) { }
        public void UpdateProgress(uint total, uint completed) => CheckCancel();
        public void ResetTimer() { }
        public void PauseTimer() { }
        public void ResumeTimer() { }
    }
}

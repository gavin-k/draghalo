using System.Runtime.InteropServices;
using DropHalo.Core.Actions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Ole;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;

namespace DropHalo.Shell;

/// <summary>
/// The system folder picker on an STA thread of its own, with no owner: DropHalo has no window of its
/// own in front when a drop lands. Windows keeps the foreground from a process the user is not working
/// in, so an empty input event right before the dialog opens lets it come up in front of Explorer.
/// </summary>
public static class FolderDialog
{
    /// <summary>HRESULT_FROM_WIN32(ERROR_CANCELLED): the user closed the dialog.</summary>
    private const int Canceled = unchecked((int)0x800704C7);

    /// <summary>The picked folder, or null when the user closes the dialog. Canceling closes it too.</summary>
    public static Task<string?> PickAsync(FolderPrompt prompt, CancellationToken cancellationToken)
        => OnStaAsync("FolderDialog", () => Pick(prompt, cancellationToken), cancellationToken);

    internal static Task<T> OnStaAsync<T>(string name, Func<T> show, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var answer = show();
                cancellationToken.ThrowIfCancellationRequested();
                done.SetResult(answer);
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
        { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static unsafe string? Pick(FolderPrompt prompt, CancellationToken cancellationToken)
    {
        PInvoke.CoCreateInstance(typeof(FileOpenDialog).GUID, null, CLSCTX.CLSCTX_INPROC_SERVER, out IFileOpenDialog dialog).ThrowOnFailure();
        try
        {
            // The client GUID comes first: it picks the remembered folder the rest of the setup starts from.
            dialog.SetClientGuid(prompt.Memory);
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM
                | FILEOPENDIALOGOPTIONS.FOS_PATHMUSTEXIST | FILEOPENDIALOGOPTIONS.FOS_NOCHANGEDIR);
            if (Directory.Exists(prompt.StartFolder))
            {
                var startItem = Item(prompt.StartFolder);
                try
                {
                    dialog.SetDefaultFolder(startItem);
                }
                finally
                {
                    Marshal.ReleaseComObject(startItem);
                }
            }

            dialog.SetTitle(prompt.Title);
            dialog.SetOkButtonLabel(prompt.OkLabel);
            cancellationToken.ThrowIfCancellationRequested();

            // A zero mouse event moves and clicks nothing, but makes this process the one that sent the
            // last input, which Windows lets bring a window to the front.
            PInvoke.SendInput([new INPUT { type = INPUT_TYPE.INPUT_MOUSE }], sizeof(INPUT));
            using var events = new FolderEvents(cancellationToken);
            dialog.Advise(events, out var cookie);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                dialog.Show(HWND.Null);
            }
            catch (COMException ex) when (ex.HResult == Canceled)
            {
                return null;
            }
            finally
            {
                events.Dispose();
                dialog.Unadvise(cookie);
            }

            dialog.GetResult(out var item);
            try
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
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    /// <summary>Bind cancellation after the native dialog has an HWND. A token canceled earlier closes it then.</summary>
    internal sealed class CloseOnCancellation(CancellationToken token) : IDisposable
    {
        private CancellationTokenRegistration? _registration;

        public void Attach(HWND window)
        {
            if (!window.IsNull)
            {
                _registration ??= token.Register(() => PInvoke.PostMessage(window, PInvoke.WM_CLOSE, 0, 0));
            }
        }

        public void Dispose()
        {
            _registration?.Dispose();
            _registration = null;
        }
    }

    private sealed class FolderEvents(CancellationToken token) : IFileDialogEvents, IDisposable
    {
        private readonly CloseOnCancellation _close = new(token);

        public void OnFolderChange(IFileDialog dialog)
        {
            ((IOleWindow)dialog).GetWindow(out var hwnd);
            _close.Attach(hwnd);
        }

        public void OnFileOk(IFileDialog dialog) { }
        public void OnFolderChanging(IFileDialog dialog, IShellItem folder) { }
        public void OnSelectionChange(IFileDialog dialog) { }
        public unsafe void OnShareViolation(IFileDialog dialog, IShellItem item, FDE_SHAREVIOLATION_RESPONSE* response) => *response = default;
        public void OnTypeChange(IFileDialog dialog) { }
        public unsafe void OnOverwrite(IFileDialog dialog, IShellItem item, FDE_OVERWRITE_RESPONSE* response) => *response = default;
        public void Dispose() => _close.Dispose();
    }

    private static IShellItem Item(string path)
    {
        PInvoke.SHCreateItemFromParsingName(Path.GetFullPath(path), null, typeof(IShellItem).GUID, out var item).ThrowOnFailure();
        return (IShellItem)item;
    }
}

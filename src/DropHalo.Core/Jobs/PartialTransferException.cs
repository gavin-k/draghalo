namespace DropHalo.Core.Jobs;

/// <summary>
/// A move or copy that was canceled after some of its items had arrived. It is a cancellation that says what it
/// left behind, so the caller can report those items and offer to put them back.
/// </summary>
public sealed class PartialTransferException(IReadOnlyList<FileMove> transferred, CancellationToken cancellationToken)
    : OperationCanceledException("The transfer was canceled after some items had arrived.", cancellationToken)
{
    /// <summary>The items that arrived, with their actual destinations.</summary>
    public IReadOnlyList<FileMove> Transferred { get; } = transferred;
}

namespace DropHalo.Core.Jobs;

/// <summary>File operations that must go through the Shell so the user can see and reverse them.</summary>
public interface IFileOperations
{
    /// <summary>Send files to the Recycle Bin. Never a permanent delete; files that are already gone are ignored.</summary>
    Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Moves the items and returns their actual destinations, including any names the Shell numbered.</summary>
    Task<IReadOnlyList<FileMove>> MoveAsync(IReadOnlyList<FileMove> moves, CancellationToken cancellationToken = default);

    /// <summary>Copies the items and returns their actual destinations, including any names the Shell numbered.</summary>
    Task<IReadOnlyList<FileMove>> CopyAsync(IReadOnlyList<FileMove> copies, CancellationToken cancellationToken = default);
}

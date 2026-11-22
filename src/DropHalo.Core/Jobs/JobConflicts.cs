namespace DropHalo.Core.Jobs;

/// <summary>Asks the user what to do with an output whose name is taken, for <see cref="ConflictPolicy.Ask"/>.</summary>
public interface IConflictPrompt
{
    /// <summary>Asks about <paramref name="target"/>, which already exists. Closing the question is a skip.</summary>
    /// <param name="more">The job has more items after this one, so the answer may be kept for them.</param>
    /// <returns>Replace, number or skip; any other policy counts as skip.</returns>
    Task<ConflictAnswer> AskAsync(string target, bool more, CancellationToken cancellationToken);
}

/// <param name="Policy"><see cref="ConflictPolicy.OverwriteToRecycleBin"/>, <see cref="ConflictPolicy.AutoNumber"/> or <see cref="ConflictPolicy.Skip"/>.</param>
/// <param name="ForTheRest">Do the same for the rest of this job without asking again.</param>
public sealed record ConflictAnswer(ConflictPolicy Policy, bool ForTheRest = false);

/// <summary>
/// One job's taken names under the replace and Ask policies. A source of the job, a folder holding one, and
/// an output the job already wrote are never replaced or asked about: those names are numbered instead.
/// Replacing recycles the old item before the new one moves in; undoing the job recycles the new outputs
/// and does not bring the old items back.
/// </summary>
/// <param name="prompt">Asks for <see cref="ConflictPolicy.Ask"/>; without one, a taken name is numbered.</param>
public sealed class JobConflicts
{
    private readonly IFileOperations _files;
    private readonly IConflictPrompt? _prompt;
    private readonly CancellationToken _cancellationToken;
    private readonly HashSet<string> _ownSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, bool IsFolder)> _own = [];
    private HashSet<OutputSnapshot.ItemIdentity>? _ownIdentities;
    private readonly Lock _gate = new();
    private ConflictPolicy? _forTheRest;
    private int _replaced;

    public JobConflicts(IFileOperations files, IConflictPrompt? prompt, IReadOnlyList<string> sources, CancellationToken cancellationToken)
    {
        _files = files;
        _prompt = prompt;
        _cancellationToken = cancellationToken;
        foreach (var source in sources)
        {
            var full = Full(source);
            if (_ownSet.Add(full))
            {
                _own.Add((full, Directory.Exists(full)));
            }
        }
    }

    /// <summary>Old items this job recycled to make room for its outputs.</summary>
    public int Replaced => Volatile.Read(ref _replaced);

    /// <summary>
    /// What to do with <paramref name="target"/>, which exists, under <paramref name="policy"/>:
    /// <see cref="ConflictPolicy.OverwriteToRecycleBin"/>, <see cref="ConflictPolicy.AutoNumber"/> or <see cref="ConflictPolicy.Skip"/>.
    /// </summary>
    /// <param name="more">The job has more items after this one.</param>
    public async Task<ConflictPolicy> SettleAsync(string target, ConflictPolicy policy, bool more)
    {
        if (policy is not (ConflictPolicy.OverwriteToRecycleBin or ConflictPolicy.Ask))
        {
            return policy;
        }

        if (IsOwn(target))
        {
            return ConflictPolicy.AutoNumber;
        }

        if (policy == ConflictPolicy.OverwriteToRecycleBin)
        {
            return policy;
        }

        lock (_gate)
        {
            if (_forTheRest is { } kept)
            {
                return kept;
            }
        }

        if (_prompt is null)
        {
            return ConflictPolicy.AutoNumber;
        }

        var answer = await _prompt.AskAsync(target, more, _cancellationToken).ConfigureAwait(false);
        _cancellationToken.ThrowIfCancellationRequested();
        var settled = answer.Policy is ConflictPolicy.OverwriteToRecycleBin or ConflictPolicy.AutoNumber ? answer.Policy : ConflictPolicy.Skip;
        if (answer.ForTheRest)
        {
            lock (_gate)
            {
                _forTheRest = settled;
            }
        }

        return settled;
    }

    /// <summary>
    /// Recycles <paramref name="target"/> to make room. Fails if it is still there afterwards, as when the
    /// user declines to delete for good an item on a drive without a Recycle Bin.
    /// </summary>
    public async Task ReplaceAsync(string target)
    {
        if (IsOwn(target))
        {
            throw new InvalidOperationException($"{target} belongs to this job and is never replaced.");
        }

        await _files.RecycleAsync([target], _cancellationToken).ConfigureAwait(false);
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException(L.T("{0} 没能移到回收站，没有替换它", Path.GetFileName(target)));
        }

        Interlocked.Increment(ref _replaced);
    }

    /// <summary>Notes an output this job wrote, so a later item never replaces it.</summary>
    public void Wrote(string path)
    {
        var full = Full(path);
        var isFolder = Directory.Exists(full);
        lock (_gate)
        {
            if (_ownSet.Add(full))
            {
                _own.Add((full, isFolder));
                if (_ownIdentities is not null && OutputSnapshot.TryGetIdentity(full, out var id))
                {
                    _ownIdentities.Add(id);
                }
            }
        }
    }

    private bool IsOwn(string target)
    {
        var full = Full(target);
        lock (_gate)
        {
            if (_ownSet.Contains(full))
            {
                return true;
            }

            foreach (var (own, isFolder) in _own)
            {
                if (IsInside(own, full) || (isFolder && IsInside(full, own)))
                {
                    return true;
                }
            }

            if (_ownIdentities is null)
            {
                _ownIdentities = [];
                foreach (var (own, _) in _own)
                {
                    if (OutputSnapshot.TryGetIdentity(own, out var ownId))
                    {
                        _ownIdentities.Add(ownId);
                    }
                }
            }

            return OutputSnapshot.TryGetIdentity(full, out var targetId) && _ownIdentities.Contains(targetId);
        }
    }

    private static bool IsInside(string path, string folder)
    {
        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

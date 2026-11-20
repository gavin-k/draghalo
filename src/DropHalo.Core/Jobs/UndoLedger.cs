namespace DropHalo.Core.Jobs;

/// <summary>
/// Remembers finished jobs that can be reversed and reverses them through <see cref="IFileOperations"/>.
/// Source files are never touched: a conversion is undone by recycling what it produced.
/// </summary>
public sealed class UndoLedger(IFileOperations files)
{
    private readonly List<Job> _entries = [];
    // A null snapshot set claims the undo without moving its entry in completion order.
    private readonly Dictionary<Guid, Dictionary<string, OutputSnapshot?>?> _snapshots = [];

    /// <summary>What it reverses through; jobs recycle the items they replace through it too.</summary>
    public IFileOperations Files => files;
    private readonly Lock _gate = new();

    /// <summary>The newest job that can still be undone.</summary>
    public Job? Latest
    {
        get
        {
            lock (_gate)
            {
                return _entries.FindLast(job => _snapshots[job.Id] is not null);
            }
        }
    }

    public void Record(Job job) => Record(job, job.Request.Written);

    internal void Record(Job job, IReadOnlyDictionary<string, OutputSnapshot> written)
    {
        if (job.Undo is UndoPlan.None)
        {
            return;
        }

        var paths = job.Undo is UndoPlan.MoveBack m ? m.Moves.Select(x => x.To) : ((UndoPlan.RecycleOutputs)job.Undo).Paths;
        var snapshots = paths.ToDictionary(p => p, p => written.GetValueOrDefault(p) ?? OutputSnapshot.Capture(p), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (_snapshots.TryAdd(job.Id, snapshots))
            {
                _entries.Add(job);
            }
        }
    }

    /// <summary>Reverses <paramref name="job"/> once. Returns false if it was never recorded or is already undone.</summary>
    public async Task<bool> UndoAsync(Job job, CancellationToken cancellationToken = default)
    {
        Dictionary<string, OutputSnapshot?> snapshots;
        lock (_gate)
        {
            if (!_snapshots.TryGetValue(job.Id, out var available) || available is null)
            {
                return false;
            }

            snapshots = available;
            _snapshots[job.Id] = null;
        }

        try
        {
            foreach (var (path, snapshot) in snapshots)
            {
                if (snapshot is null || !snapshot.Matches(path))
                {
                    throw new IOException(L.T("{0} 已变化或无法核实，未撤销", Path.GetFileName(path)));
                }
            }

            await (job.Undo switch
            {
                UndoPlan.RecycleOutputs r => files.RecycleAsync(r.Paths, cancellationToken),
                UndoPlan.MoveBack m => files.MoveAsync([.. m.Moves.Select(x => new FileMove(x.To, x.From))], cancellationToken),
                _ => throw new NotSupportedException($"Undo for {job.Undo.GetType().Name} is not implemented yet."),
            }).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _snapshots[job.Id] = snapshots;
            }

            throw;
        }

        lock (_gate)
        {
            _entries.Remove(job);
            _snapshots.Remove(job.Id);
        }

        return true;
    }
}

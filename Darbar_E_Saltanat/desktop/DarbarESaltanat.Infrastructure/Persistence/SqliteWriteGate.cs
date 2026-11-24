namespace DarbarESaltanat.Infrastructure.Persistence;

/// <summary>
/// Single-writer pattern: SQLite allows one writer at a time. All SaveChanges calls in the process
/// serialise through this gate, so UI writes and the sync worker never fight over the write lock.
/// Never hold the gate (or a transaction) across a network call.
/// </summary>
public sealed class SqliteWriteGate
{
    private readonly SemaphoreSlim _sem = new(1, 1);
    public async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_sem);
    }
    private sealed class Releaser(SemaphoreSlim s) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) s.Release(); }
    }
}

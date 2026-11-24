namespace DarbarESaltanat.Application.Sync;

/// <summary>
/// Process-wide "local data was just committed" signal. The local database raises it after a SaveChanges that queued
/// outbox rows; the sync worker listens and pushes to the server right away instead of waiting for its next timer tick.
/// It carries no data and never throws into the caller, so a slow or broken sync can never fail a sale.
/// </summary>
public static class SyncSignal
{
    public static event Action? LocalChangesCommitted;

    public static void Raise()
    {
        try { LocalChangesCommitted?.Invoke(); }
        catch { /* a listener failure must never break a committed save */ }
    }
}

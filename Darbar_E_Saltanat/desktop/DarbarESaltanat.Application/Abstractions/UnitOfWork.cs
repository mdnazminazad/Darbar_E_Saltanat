using System.Linq.Expressions;
using DarbarESaltanat.Domain.Common;

namespace DarbarESaltanat.Application.Abstractions;

/// <summary>
/// One short-lived local database transaction. Entities returned are tracked: modify them, Add/Delete others,
/// then <see cref="CommitAsync"/> writes everything — business rows AND their sync_outbox entries — atomically.
/// Disposing without committing discards all changes. Never keep a unit of work open across a network call.
/// </summary>
public interface IUnitOfWork : IAsyncDisposable
{
    Task<T?> FindAsync<T>(string uuid, CancellationToken ct = default) where T : SyncEntity;
    Task<List<T>> ListAsync<T>(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default) where T : SyncEntity;
    Task<int> CountAsync<T>(Expression<Func<T, bool>> predicate, bool includeDeleted = false, CancellationToken ct = default) where T : SyncEntity;
    void Add<T>(T entity) where T : SyncEntity;
    /// <summary>Soft delete (sets deleted_at; replicated as a DELETE).</summary>
    void Delete<T>(T entity) where T : SyncEntity;
    Task CommitAsync(CancellationToken ct = default);
}

public interface IUnitOfWorkFactory
{
    IUnitOfWork Begin();
}

using System.Linq.Expressions;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace DarbarESaltanat.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="IUnitOfWork"/>: one DbContext, one SaveChanges = one SQLite transaction.</summary>
public sealed class EfUnitOfWork(AppDbContext db) : IUnitOfWork
{
    public async Task<T?> FindAsync<T>(string uuid, CancellationToken ct = default) where T : SyncEntity
    {
        // Entities added in this unit of work (not yet committed) are found too.
        var local = db.Set<T>().Local.FirstOrDefault(e => e.Uuid == uuid && e.DeletedAt == null);
        return local ?? await db.Set<T>().FirstOrDefaultAsync(e => e.Uuid == uuid, ct);
    }

    public Task<List<T>> ListAsync<T>(Expression<Func<T, bool>>? predicate = null, CancellationToken ct = default) where T : SyncEntity =>
        (predicate is null ? db.Set<T>() : db.Set<T>().Where(predicate)).ToListAsync(ct);

    public Task<int> CountAsync<T>(Expression<Func<T, bool>> predicate, bool includeDeleted = false, CancellationToken ct = default) where T : SyncEntity =>
        (includeDeleted ? db.Set<T>().IgnoreQueryFilters() : db.Set<T>()).CountAsync(predicate, ct);

    public void Add<T>(T entity) where T : SyncEntity => db.Set<T>().Add(entity);

    public void Delete<T>(T entity) where T : SyncEntity => db.Set<T>().Remove(entity);

    public Task CommitAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public ValueTask DisposeAsync() => db.DisposeAsync();
}

public sealed class EfUnitOfWorkFactory(IAppDbContextFactory factory) : IUnitOfWorkFactory
{
    public IUnitOfWork Begin() => new EfUnitOfWork(factory.Create());
}

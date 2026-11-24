using System.Text.Json;
using DarbarESaltanat.Application.Abstractions;
using DarbarESaltanat.Application.Auth;
using DarbarESaltanat.Domain.Entities;

namespace DarbarESaltanat.Application.Common;

public sealed class AuditFactory(ICurrentSession session, IDeviceContext device, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public AuditLog Create(string action, string? entityType, string? entityUuid, object? oldValues = null, object? newValues = null) => new()
    {
        UserUuid = session.Current?.UserUuid,
        Action = action,
        EntityType = entityType,
        EntityUuid = entityUuid,
        OldValues = oldValues is null ? null : JsonSerializer.Serialize(oldValues, Json),
        NewValues = newValues is null ? null : JsonSerializer.Serialize(newValues, Json),
        OccurredAt = clock.UtcNow,
        BranchUuid = device.BranchUuid,
        UserAgent = $"desktop:{device.DeviceId}",
    };
}

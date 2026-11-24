namespace DarbarESaltanat.Application.Common;

/// <summary>Outcome of an application operation. UI shows <see cref="Error"/> to the user as-is.</summary>
public sealed record Result(bool Succeeded, string? Error = null, string? ErrorCode = null)
{
    public static Result Ok() => new(true);
    public static Result Fail(string error, string? code = null) => new(false, error, code);
}

public sealed record Result<T>(bool Succeeded, T? Value, string? Error = null, string? ErrorCode = null)
{
    public static Result<T> Ok(T value) => new(true, value);
    public static Result<T> Fail(string error, string? code = null) => new(false, default, error, code);
}

/// <summary>Thrown when the signed-in user lacks a permission. Caught by the global handler.</summary>
public sealed class PermissionDeniedException(string permission)
    : Exception($"You do not have permission to perform this action ({permission}).")
{
    public string Permission { get; } = permission;
}

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DarbarESaltanat.Application.Abstractions;

namespace DarbarESaltanat.Infrastructure.Security;

/// <summary>bcrypt, compatible with the server's bcryptjs hashes ($2a$/$2b$).</summary>
public sealed class BcryptPasswordHasher(int workFactor = 12) : IPasswordHasher
{
    // Pre-computed hash so verifying against "no user" costs the same time as a real check.
    private const string Dummy = "$2a$12$og47Uw6Ne.GcFPHxrYNDRe.s/HDc4Tq6hJZnTlh8VEKaNXu014WO.";
    public string Hash(string secret) => BCrypt.Net.BCrypt.HashPassword(secret, workFactor);
    public bool Verify(string secret, string? hash)
    {
        try { return BCrypt.Net.BCrypt.Verify(secret, hash ?? Dummy) && hash is not null; }
        catch (BCrypt.Net.SaltParseException) { return false; }
    }
}

public sealed class SystemClock : IClock { public DateTime UtcNow => DateTime.UtcNow; }

/// <summary>Windows DPAPI (current user scope) for the device token stored in SQLite.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DarbarESaltanat.DeviceToken.v1");
    public string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));
    public string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return null; } // copied to another PC/user: must re-enter the token
    }
}

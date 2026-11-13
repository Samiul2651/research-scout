using System.Security.Cryptography;
using System.Text;

namespace ResearchScout.Api;

/// <summary>
/// Checks the X-Admin-Password header against ADMIN_PASSWORD. Both are hashed first and compared in constant time,
/// so the check takes the same time whether the first or last character is wrong (no timing leak).
/// If ADMIN_PASSWORD is not set, admin endpoints are disabled.
/// </summary>
internal sealed class AdminAuth(string? adminPassword)
{
    public const string HeaderName = "X-Admin-Password";

    private readonly byte[]? _expectedHash = string.IsNullOrEmpty(adminPassword) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(adminPassword));

    public bool Enabled => _expectedHash is not null;

    public bool IsAdmin(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_expectedHash is null || !request.Headers.TryGetValue(HeaderName, out var given) || string.IsNullOrEmpty(given))
        {
            return false;
        }

        var givenHash = SHA256.HashData(Encoding.UTF8.GetBytes(given.ToString()));
        return CryptographicOperations.FixedTimeEquals(givenHash, _expectedHash);
    }
}

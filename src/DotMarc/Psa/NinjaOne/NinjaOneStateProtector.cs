using System.Security.Cryptography;
using System.Text.Json;
using DotMarc.Audit;
using Microsoft.AspNetCore.DataProtection;

namespace DotMarc.Psa.NinjaOne;

/// <summary>The OAuth state for connecting NinjaOne: signed, bound to the user who started it, and good for 10 minutes.
/// That stops a sign-in started by one person, or replayed later, from connecting dotMARC to someone else's NinjaOne
/// account.</summary>
public sealed class NinjaOneStateProtector(IDataProtectionProvider dataProtectionProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("DotMarc.NinjaOne.ConnectState.v1");

    public static string UserKey(AuditActor actor) => actor.ObjectId ?? actor.Email ?? actor.Name;

    public string Protect(string userKey, DateTimeOffset nowUtc) =>
        _protector.Protect(JsonSerializer.Serialize(new ConnectState(userKey, nowUtc.Add(Lifetime))));

    public bool IsValid(string state, string userKey, DateTimeOffset nowUtc)
    {
        try
        {
            var decoded = JsonSerializer.Deserialize<ConnectState>(_protector.Unprotect(state));
            return decoded is not null && decoded.UserKey == userKey && decoded.ExpiresAtUtc > nowUtc;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            return false;
        }
    }

    private sealed record ConnectState(string UserKey, DateTimeOffset ExpiresAtUtc);
}

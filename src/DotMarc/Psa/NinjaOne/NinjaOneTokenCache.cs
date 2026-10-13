using DotMarc.Notifications;

namespace DotMarc.Psa.NinjaOne;

/// <summary>Holds NinjaOne's access token in memory and gets a new one with the saved refresh token. Each replica keeps
/// its own: NinjaOne hands back the same refresh token on each use, so replicas refreshing side by side don't trip over
/// each other. If NinjaOne ever returns a different one (sliding tokens may), it is saved.</summary>
public sealed class NinjaOneTokenCache(ISecretStore secretStore, TimeProvider timeProvider)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string Key, string Token, DateTimeOffset ExpiresUtc)? _cached;

    public async Task<string> GetTokenAsync(HttpClient httpClient, NinjaOneSettings settings, bool forceRefresh, CancellationToken cancellationToken)
    {
        var key = $"{settings.Instance}|{settings.ClientId}";
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cached is { } cached && cached.Key == key && timeProvider.GetUtcNow() < cached.ExpiresUtc)
            {
                return cached.Token;
            }

            var clientSecret = await secretStore.GetSecretAsync(NinjaOneSettings.ClientSecretKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The NinjaOne client secret isn't saved.");
            var refreshToken = await secretStore.GetSecretAsync(NinjaOneSettings.RefreshTokenKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(refreshToken) || refreshToken == NinjaOneSettings.ClearedRefreshToken)
            {
                throw new NinjaOneConnectionLostException("dotMARC has no saved NinjaOne sign-in.");
            }

            var response = await NinjaOneOAuth.RequestTokenAsync(httpClient, settings.Instance!, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = settings.ClientId!,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refreshToken,
            }, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(response.RefreshToken) && response.RefreshToken != refreshToken)
            {
                await secretStore.SetSecretAsync(NinjaOneSettings.RefreshTokenKey, response.RefreshToken, cancellationToken).ConfigureAwait(false);
            }

            // A minute early, so a call starting just before expiry doesn't race a 401.
            _cached = (key, response.AccessToken, timeProvider.GetUtcNow().AddSeconds(response.ExpiresInSeconds - 60));
            return response.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forgets the access token, after a 401, a reconnect or a disconnect.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cached = null;
        }
        finally
        {
            _lock.Release();
        }
    }
}

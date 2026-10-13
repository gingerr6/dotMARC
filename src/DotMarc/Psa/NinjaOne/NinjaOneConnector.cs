using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Psa.NinjaOne;

public enum NinjaOneConnectOutcome { Connected, Cancelled, InvalidState, NotConfigured, Refused, NoRefreshToken, Failed }

/// <summary>Connects dotMARC to NinjaOne with the authorization code flow, behind /psa/ninjaone/connect and
/// /psa/ninjaone/callback.</summary>
public sealed class NinjaOneConnector(
    HttpClient httpClient, IDbContextFactory<DotMarcDbContext> dbFactory, ISecretStore secretStore, NinjaOneStateProtector stateProtector,
    NinjaOneTokenCache tokenCache, IAlertingService alerting, TimeProvider timeProvider, ILogger<NinjaOneConnector> logger)
{
    public const string CallbackPath = "/psa/ninjaone/callback";

    public static string RedirectUri(HttpRequest request) => $"{request.Scheme}://{request.Host}{CallbackPath}";

    public static string OutcomeKey(NinjaOneConnectOutcome outcome) => outcome switch
    {
        NinjaOneConnectOutcome.Connected => "connected",
        NinjaOneConnectOutcome.Cancelled => "cancelled",
        NinjaOneConnectOutcome.InvalidState => "expired",
        NinjaOneConnectOutcome.NotConfigured => "not-configured",
        NinjaOneConnectOutcome.Refused => "refused",
        NinjaOneConnectOutcome.NoRefreshToken => "no-refresh-token",
        _ => "failed"
    };

    /// <summary>Null until the instance, client ID and client secret are saved.</summary>
    public async Task<string?> BuildAuthorizationUrlAsync(string redirectUri, string state, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        return IsConfigured(settings) ? NinjaOneOAuth.AuthorizeUrl(settings, redirectUri, state) : null;
    }

    public async Task<NinjaOneConnectOutcome> CompleteAsync(AuditActor actor, string? state, string? code, string? error, string redirectUri, CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        if (state is null || !stateProtector.IsValid(state, NinjaOneStateProtector.UserKey(actor), nowUtc))
        {
            return NinjaOneConnectOutcome.InvalidState;
        }

        if (error is not null || string.IsNullOrEmpty(code))
        {
            return NinjaOneConnectOutcome.Cancelled;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var clientSecret = await secretStore.GetSecretAsync(NinjaOneSettings.ClientSecretKey, cancellationToken).ConfigureAwait(false);
        if (!IsConfigured(settings) || string.IsNullOrEmpty(clientSecret))
        {
            return NinjaOneConnectOutcome.NotConfigured;
        }

        NinjaOneTokenResponse token;
        try
        {
            token = await NinjaOneOAuth.RequestTokenAsync(httpClient, settings.Instance!, new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = settings.ClientId!,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (NinjaOneConnectionLostException exception)
        {
            logger.LogWarning("NinjaOne refused the sign-in code: {Reason}", exception.Message);
            return NinjaOneConnectOutcome.Refused;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
        {
            logger.LogWarning(exception, "Exchanging the NinjaOne sign-in code failed");
            return NinjaOneConnectOutcome.Failed;
        }

        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            logger.LogWarning("NinjaOne signed in but returned no refresh token. The client app needs the Refresh token grant type.");
            return NinjaOneConnectOutcome.NoRefreshToken;
        }

        await NinjaOneSettingsService.ConnectAsync(context, actor, secretStore, token.RefreshToken, nowUtc, cancellationToken).ConfigureAwait(false);
        await tokenCache.ClearAsync(cancellationToken).ConfigureAwait(false);
        await alerting.ResolvePsaConnectionLostAsync(PsaKind.NinjaOne, cancellationToken).ConfigureAwait(false);
        return NinjaOneConnectOutcome.Connected;
    }

    private static bool IsConfigured(NinjaOneSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.Instance) && !string.IsNullOrWhiteSpace(settings.ClientId) && settings.ClientSecretConfigured;
}

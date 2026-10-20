using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Identity.Client;

namespace DotMarc.Graph;

public enum GuestInviteOutcome { Invited, Refused, NotPermitted, Failed }

/// <summary>Detail is Entra's own explanation when it refused the invite, such as the person already being a member.</summary>
public sealed record GuestInviteResult(GuestInviteOutcome Outcome, string? Detail = null);

public interface IEntraGuestInviter
{
    /// <summary>False where there's no Entra tenant to invite into, as in the demo.</summary>
    bool IsAvailable { get; }

    /// <summary>Invites someone into the tenant as a guest, sending Microsoft's invitation email, which brings them back
    /// to redirectUrl once they accept. Never throws for a refusal or a failure: granting access in dotMARC has already
    /// happened, so the caller only needs to say how the invite went.</summary>
    Task<GuestInviteResult> InviteAsync(string email, string redirectUrl, CancellationToken cancellationToken);
}

/// <summary>Invites through Microsoft Graph as the dashboard sign-in app (the EntraId settings), app-only, which needs
/// that app to hold the User.Invite.All application permission with admin consent. The mailbox app is left with mail
/// permissions only.</summary>
public sealed class EntraGuestInviter : IEntraGuestInviter
{
    public const string HttpClientName = "EntraGuestInviter";
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Func<CancellationToken, Task<string>> _acquireToken;
    private readonly ILogger<EntraGuestInviter> _logger;

    public EntraGuestInviter(IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<EntraGuestInviter> logger)
    {
        var section = configuration.GetSection("EntraId");
        var app = ConfidentialClientApplicationBuilder.Create(section["ClientId"])
            .WithClientSecret(section["ClientSecret"])
            .WithAuthority($"https://login.microsoftonline.com/{section["TenantId"]}")
            .Build();
        _acquireToken = async cancellationToken =>
            (await app.AcquireTokenForClient(Scopes).ExecuteAsync(cancellationToken).ConfigureAwait(false)).AccessToken;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>For tests: the token call is supplied.</summary>
    internal EntraGuestInviter(Func<CancellationToken, Task<string>> acquireToken, IHttpClientFactory httpClientFactory, ILogger<EntraGuestInviter> logger)
    {
        _acquireToken = acquireToken;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsAvailable => true;

    public async Task<GuestInviteResult> InviteAsync(string email, string redirectUrl, CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await _acquireToken(cancellationToken).ConfigureAwait(false);
        }
        catch (MsalServiceException ex)
        {
            // A wrong client secret (AADSTS7000215) or a deleted app lands here.
            _logger.LogWarning(ex, "Couldn't get a Graph token for the dashboard app to invite a guest.");
            return new GuestInviteResult(GuestInviteOutcome.Failed, ex.Message);
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "invitations")
        {
            Content = JsonContent.Create(new
            {
                invitedUserEmailAddress = email,
                inviteRedirectUrl = redirectUrl,
                sendInvitationMessage = true,
            }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Couldn't reach Graph to invite a guest.");
            return new GuestInviteResult(GuestInviteOutcome.Failed, ex.Message);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return new GuestInviteResult(GuestInviteOutcome.Invited);
            }

            var detail = await ErrorMessageAsync(response, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Graph refused to invite a guest ({StatusCode}): {Detail}", (int)response.StatusCode, detail);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new GuestInviteResult(GuestInviteOutcome.NotPermitted, detail),
                HttpStatusCode.BadRequest => new GuestInviteResult(GuestInviteOutcome.Refused, detail),
                _ => new GuestInviteResult(GuestInviteOutcome.Failed, detail),
            };
        }
    }

    private static async Task<string?> ErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            return body.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Where dotMARC has no Entra tenant, as in the demo.</summary>
public sealed class UnavailableEntraGuestInviter : IEntraGuestInviter
{
    public bool IsAvailable => false;

    public Task<GuestInviteResult> InviteAsync(string email, string redirectUrl, CancellationToken cancellationToken) =>
        Task.FromResult(new GuestInviteResult(GuestInviteOutcome.Failed, "Inviting guests isn't available here."));
}

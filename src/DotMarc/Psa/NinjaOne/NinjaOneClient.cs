using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace DotMarc.Psa.NinjaOne;

/// <summary>NinjaOne's public API, v2, signed in as the user who connected dotMARC.</summary>
public sealed class NinjaOneClient(HttpClient httpClient, NinjaOneTokenCache tokenCache, INinjaOneConnectionRecorder recorder) : INinjaOneClient
{
    private const int PageSize = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PsaCompany>> ListOrganizationsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default)
    {
        var organisations = new List<PsaCompany>();
        int? after = null;
        while (true)
        {
            var path = after is { } lastId ? $"organizations?pageSize={PageSize}&after={lastId}" : $"organizations?pageSize={PageSize}";
            var page = await GetJsonAsync<List<IdName>>(settings, path, cancellationToken).ConfigureAwait(false) ?? [];
            organisations.AddRange(page.Select(entry => new PsaCompany(entry.Id.ToString(CultureInfo.InvariantCulture), entry.Name)));

            // A short page is the last one.
            if (page.Count < PageSize)
            {
                break;
            }

            after = page[^1].Id;
        }

        return organisations.OrderBy(organisation => organisation.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<PsaOption>> ListTicketFormsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default)
    {
        var forms = await GetJsonAsync<List<IdName>>(settings, "ticketing/ticket-form", cancellationToken).ConfigureAwait(false) ?? [];
        return forms.Select(form => new PsaOption(form.Id, form.Name)).OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<NinjaOneStatus>> ListStatusesAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) =>
        await GetJsonAsync<List<NinjaOneStatus>>(settings, "ticketing/statuses", cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<string> CreateTicketAsync(NinjaOneSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default)
    {
        var body = new CreateTicketBody(
            int.Parse(request.CompanyId, CultureInfo.InvariantCulture),
            settings.TicketFormId!.Value,
            Truncate(request.Title, 200),
            new CommentBody(request.Body, false),
            settings.NewStatusId!.Value.ToString(CultureInfo.InvariantCulture),
            settings.TicketType,
            settings.Priority,
            settings.Severity);
        using var response = await SendAsync(settings, () => new HttpRequestMessage(HttpMethod.Post, Url(settings, "ticketing/ticket")) { Content = JsonContent.Create(body, options: JsonOptions) }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "POST ticketing/ticket", cancellationToken).ConfigureAwait(false);
        var created = await response.Content.ReadFromJsonAsync<IdOnly>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("NinjaOne created a ticket but didn't say its id.");
        return created.Id.ToString(CultureInfo.InvariantCulture);
    }

    public async Task<NinjaOneTicket?> GetTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(settings, () => new HttpRequestMessage(HttpMethod.Get, Url(settings, $"ticketing/ticket/{Uri.EscapeDataString(ticketId)}")), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "GET ticketing/ticket", cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<NinjaOneTicket>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddCommentAsync(NinjaOneSettings settings, string ticketId, string body, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(settings, () =>
        {
            var content = new MultipartFormDataContent
            {
                { new StringContent(JsonSerializer.Serialize(new CommentBody(body, false), JsonOptions), Encoding.UTF8, "application/json"), "comment" }
            };
            return new HttpRequestMessage(HttpMethod.Post, Url(settings, $"ticketing/ticket/{Uri.EscapeDataString(ticketId)}/comment")) { Content = content };
        }, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "POST ticketing/ticket comment", cancellationToken).ConfigureAwait(false);
    }

    public async Task ResolveTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var ticket = await GetTicketAsync(settings, ticketId, cancellationToken).ConfigureAwait(false);
            if (ticket is null || ticket.Deleted || ticket.IsResolvedOrClosed)
            {
                return;
            }

            // NinjaOne's update replaces the ticket, so everything it requires goes back as it was, with the new status
            // and the version just read.
            var update = new UpdateTicketBody(ticket.ClientId, ticket.TicketFormId, ticket.Subject, ticket.Type, ticket.Priority, ticket.Severity,
                ticket.RequesterUid, NinjaOneOptions.ResolvedStatusId.ToString(CultureInfo.InvariantCulture), ticket.Version);
            using var response = await SendAsync(settings, () => new HttpRequestMessage(HttpMethod.Put, Url(settings, $"ticketing/ticket/{Uri.EscapeDataString(ticketId)}")) { Content = JsonContent.Create(update, options: JsonOptions) }, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            // NinjaOne answers a stale version with 403 and this result code (checked against a live instance).
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var versionConflict = text.Contains("ticket_updated_by_another_user", StringComparison.Ordinal);
            if (attempt == 1 && versionConflict)
            {
                continue;
            }

            throw Failure(response.StatusCode, "PUT ticketing/ticket", text);
        }
    }

    public async Task RefreshConnectionAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default)
    {
        await GetTokenAsync(settings, forceRefresh: true, cancellationToken).ConfigureAwait(false);
        await recorder.RefreshedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetJsonAsync<T>(NinjaOneSettings settings, string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(settings, () => new HttpRequestMessage(HttpMethod.Get, Url(settings, path)), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"GET {path.Split('?')[0]}", cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends with the access token. A 401 drops it and tries once more with a fresh one; a second 401 means
    /// NinjaOne no longer accepts this sign-in.</summary>
    private async Task<HttpResponseMessage> SendAsync(NinjaOneSettings settings, Func<HttpRequestMessage> buildRequest, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var token = await GetTokenAsync(settings, forceRefresh: false, cancellationToken).ConfigureAwait(false);
            using var request = buildRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            response.Dispose();
            await tokenCache.ClearAsync(cancellationToken).ConfigureAwait(false);
            if (attempt == 2)
            {
                const string reason = "NinjaOne refused dotMARC's access token twice in a row.";
                await recorder.ConnectionLostAsync(reason, cancellationToken).ConfigureAwait(false);
                throw new NinjaOneConnectionLostException(reason);
            }
        }
    }

    private async Task<string> GetTokenAsync(NinjaOneSettings settings, bool forceRefresh, CancellationToken cancellationToken)
    {
        try
        {
            return await tokenCache.GetTokenAsync(httpClient, settings, forceRefresh, cancellationToken).ConfigureAwait(false);
        }
        catch (NinjaOneConnectionLostException exception)
        {
            await recorder.ConnectionLostAsync(exception.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response.StatusCode, what, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    // NinjaOne's error bodies carry a resultCode and an incident id, never credentials.
    private static HttpRequestException Failure(HttpStatusCode status, string what, string body)
    {
        var advice = status == HttpStatusCode.Forbidden
            ? "NinjaOne refused the request: check the connected user can manage tickets, and the client app has the Monitoring and Management scopes."
            : $"NinjaOne returned {(int)status} for {what}.";
        return new HttpRequestException(string.IsNullOrWhiteSpace(body) ? advice : $"{advice} NinjaOne said: {Truncate(body, 300)}", null, status);
    }

    private static string Url(NinjaOneSettings settings, string path) => $"https://{settings.Instance}/v2/{path}";

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    private sealed record IdName(int Id, string Name);
    private sealed record IdOnly(int Id);
    private sealed record CommentBody(string Body, bool Public);
    private sealed record CreateTicketBody(int ClientId, int TicketFormId, string Subject, CommentBody Description, string Status, string Type, string Priority, string Severity);
    private sealed record UpdateTicketBody(int? ClientId, int? TicketFormId, string Subject, string? Type, string? Priority, string? Severity, string? RequesterUid, string Status, int Version);
}

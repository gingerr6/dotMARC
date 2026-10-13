using DotMarc.Data;
using Microsoft.Extensions.Logging;

namespace DotMarc.Psa.NinjaOne;

/// <summary>NinjaOne ticketing behind the shared PSA contract.</summary>
public sealed class NinjaOneProvider(INinjaOneClient client, ILogger<NinjaOneProvider> logger) : IPsaProvider
{
    public PsaKind Kind => PsaKind.NinjaOne;

    public async Task<PsaReadiness> GetReadinessAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        return new PsaReadiness(settings.Enabled, MissingSettings(settings));
    }

    public static IReadOnlyList<string> MissingSettings(NinjaOneSettings settings)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.Instance)) missing.Add("instance");
        if (string.IsNullOrWhiteSpace(settings.ClientId)) missing.Add("client ID");
        if (!settings.ClientSecretConfigured) missing.Add("client secret");
        if (!settings.IsConnected) missing.Add("connection");
        if (settings.TicketFormId is null) missing.Add("ticket form");
        if (settings.NewStatusId is null) missing.Add("new ticket status");
        return missing;
    }

    public async Task<IReadOnlyList<PsaCompany>> ListCompaniesAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        await client.ListOrganizationsAsync(await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    public async Task<string> CreateTicketAsync(DotMarcDbContext context, PsaTicketRequest request, CancellationToken cancellationToken = default) =>
        await client.CreateTicketAsync(await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false), request, cancellationToken).ConfigureAwait(false);

    public async Task<PsaTicketState> GetTicketStateAsync(DotMarcDbContext context, string ticketId, CancellationToken cancellationToken = default)
    {
        var settings = await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var ticket = await client.GetTicketAsync(settings, ticketId, cancellationToken).ConfigureAwait(false);
        if (ticket is null || ticket.Deleted)
        {
            return PsaTicketState.Missing;
        }

        return ticket.IsResolvedOrClosed ? PsaTicketState.Closed : PsaTicketState.Open;
    }

    /// <summary>The note first, so the ticket says why it was resolved. A refused note doesn't stop the resolve.</summary>
    public async Task CloseTicketAsync(DotMarcDbContext context, string ticketId, string note, CancellationToken cancellationToken = default)
    {
        var settings = await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        try
        {
            await client.AddCommentAsync(settings, ticketId, note, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Adding the closing note to NinjaOne ticket {TicketId} failed; resolving it anyway", ticketId);
        }

        await client.ResolveTicketAsync(settings, ticketId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetTicketUrlTemplateAsync(DotMarcDbContext context, CancellationToken cancellationToken = default)
    {
        var settings = await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(settings.Instance) ? null : $"https://{settings.Instance}/#/ticketing/ticket/{{0}}";
    }
}

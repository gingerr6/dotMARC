namespace DotMarc.Psa.NinjaOne;

public interface INinjaOneClient
{
    Task<IReadOnlyList<PsaCompany>> ListOrganizationsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PsaOption>> ListTicketFormsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NinjaOneStatus>> ListStatusesAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default);
    Task<string> CreateTicketAsync(NinjaOneSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default);

    /// <summary>Null when NinjaOne has no such ticket.</summary>
    Task<NinjaOneTicket?> GetTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default);
    Task AddCommentAsync(NinjaOneSettings settings, string ticketId, string body, CancellationToken cancellationToken = default);

    /// <summary>Moves the ticket to RESOLVED. NinjaOne won't let an update close a ticket, so its own rules close it
    /// later. Does nothing when the ticket is already resolved or closed, or no longer exists.</summary>
    Task ResolveTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default);

    /// <summary>Uses the refresh token now, so it doesn't lapse, and records that it worked.</summary>
    Task RefreshConnectionAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default);
}

public sealed record NinjaOneStatus(int StatusId, string Name, string? DisplayName, int? ParentId);

public sealed record NinjaOneTicketStatus(int StatusId, string? Name, int? ParentId);

public sealed record NinjaOneTicket(
    int Id, int Version, int? ClientId, int? TicketFormId, string Subject, string? Type, string? Priority, string? Severity,
    string? RequesterUid, NinjaOneTicketStatus? Status, bool Deleted)
{
    /// <summary>A custom status counts by the built-in status it sits under.</summary>
    public bool IsResolvedOrClosed => Status is { } status
        && (IsDone(status.StatusId) || status.ParentId is { } parentId && IsDone(parentId));

    private static bool IsDone(int statusId) => statusId is NinjaOneOptions.ResolvedStatusId or NinjaOneOptions.ClosedStatusId;
}

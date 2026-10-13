namespace DotMarc.Psa.NinjaOne;

/// <summary>Singleton settings row for NinjaOne ticketing, seeded by its migration like the other PSAs. NinjaOne only
/// lets a signed-in user raise tickets, so dotMARC connects with the authorization code flow and keeps the refresh token
/// in ISecretStore under <see cref="RefreshTokenKey"/>, beside the client secret under <see cref="ClientSecretKey"/>.
/// Neither is ever on this row.</summary>
public sealed class NinjaOneSettings
{
    public const string ClientSecretKey = "NinjaOne.ClientSecret";
    public const string RefreshTokenKey = "NinjaOne.RefreshToken";

    /// <summary>What a disconnected install keeps under <see cref="RefreshTokenKey"/>. The secret store has no delete, and
    /// Key Vault may refuse an empty value, so the old token is overwritten with this.</summary>
    public const string ClearedRefreshToken = "-";

    public int Id { get; set; }
    public bool Enabled { get; set; }

    /// <summary>The instance's host, such as eu.ninjarmm.com. One of <see cref="NinjaOneOptions.Instances"/>.</summary>
    public string? Instance { get; set; }
    public string? ClientId { get; set; }
    public bool ClientSecretConfigured { get; set; }
    public bool RefreshTokenConfigured { get; set; }

    /// <summary>The dotMARC user who connected, and when. NinjaOne raises tickets as the NinjaOne user they signed in as.</summary>
    public string? ConnectedBy { get; set; }
    public DateTimeOffset? ConnectedUtc { get; set; }

    /// <summary>When the keep-alive last refreshed the sign-in.</summary>
    public DateTimeOffset? LastRefreshUtc { get; set; }

    /// <summary>Set when NinjaOne refuses the saved sign-in. NinjaOne isn't ready until someone reconnects.</summary>
    public DateTimeOffset? ConnectionLostUtc { get; set; }
    public string? ConnectionLostReason { get; set; }

    // Ticket defaults. The names sit beside the ids for display only, like ConnectWise.
    public int? TicketFormId { get; set; }
    public string? TicketFormName { get; set; }
    public int? NewStatusId { get; set; }
    public string? NewStatusName { get; set; }
    public string TicketType { get; set; } = "PROBLEM";
    public string Priority { get; set; } = "NONE";
    public string Severity { get; set; } = "NONE";

    public bool IsConnected => RefreshTokenConfigured && ConnectionLostUtc is null;
}

namespace DotMarc.Updates;

/// <summary>Singleton row shared with the server's updater (deploy/digitalocean/dotmarc-autoupdate), which runs on the host
/// every minute: it stamps LastSeenUtc, and when RequestedUtc is set it clears it, backs up and installs the newest image,
/// then writes the outcome to LastResult. The app can't replace its own container, so this row is how it asks.</summary>
public sealed class ServerUpdater
{
    public int Id { get; set; }
    public DateTimeOffset? RequestedUtc { get; set; }
    public string? RequestedBy { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public DateTimeOffset? LastResultUtc { get; set; }
    public string? LastResult { get; set; }
}

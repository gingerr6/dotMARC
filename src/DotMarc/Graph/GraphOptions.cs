using System.ComponentModel.DataAnnotations;

namespace DotMarc.Graph;

public sealed class GraphOptions
{
    public const string SectionName = "Graph";

    [Required]
    public required string ClientId { get; set; }

    [Required]
    public required string TenantId { get; set; }

    [Required]
    public required string ClientSecret { get; set; }

    [Required]
    public required string MailboxAddress { get; set; }

    public string? TlsrptMailboxAddress { get; set; }

    /// <summary>Where domains' TLS reports should go: the TLSRPT mailbox when there is one, otherwise the DMARC mailbox,
    /// whose poller reads TLS reports too.</summary>
    public string TlsReportingAddress => string.IsNullOrWhiteSpace(TlsrptMailboxAddress) ? MailboxAddress : TlsrptMailboxAddress;

    public int PollIntervalSeconds { get; set; } = 300;
}

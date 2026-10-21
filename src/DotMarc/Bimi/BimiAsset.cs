namespace DotMarc.Bimi;

/// <summary>The BIMI files dotMARC hosts for a domain: its logo (SVG Tiny PS) and, optionally, its mark certificate
/// (VMC or CMC, PEM). Served publicly by BimiEndpoints; the domain's default._bimi record points at them.</summary>
public sealed class BimiAsset
{
    public int DomainId { get; set; }
    public required string LogoSvg { get; set; }
    public string? CertificatePem { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

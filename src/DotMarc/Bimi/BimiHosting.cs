using System.Text;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Dns;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Bimi;

/// <summary>Saves and removes the BIMI files dotMARC hosts for a domain, and works out the record that points at them.
/// The files are served from dotMARC's own hostname (MtaSts:HostingHostname), so they need no hosting of their own.</summary>
public static class BimiHosting
{
    public static string LogoUrl(string hostingHostname, string domainName) => $"https://{hostingHostname}/bimi/{Uri.EscapeDataString(domainName)}/logo.svg";

    public static string CertificateUrl(string hostingHostname, string domainName) => $"https://{hostingHostname}/bimi/{Uri.EscapeDataString(domainName)}/certificate.pem";

    /// <summary>The default._bimi TXT value for the hosted files.</summary>
    public static string Record(string hostingHostname, string domainName, bool hasCertificate) =>
        $"v=BIMI1; l={LogoUrl(hostingHostname, domainName)};" + (hasCertificate ? $" a={CertificateUrl(hostingHostname, domainName)};" : "");

    /// <summary>Saves a logo that's already SVG Tiny PS (see SvgTinyPs.Prepare). Returns why it was refused, or null.</summary>
    public static async Task<string?> SaveLogoAsync(DotMarcDbContext context, AuditActor actor, Domain domain, byte[] svg, CancellationToken cancellationToken = default)
    {
        if (svg.Length > BimiChecker.MaxLogoBytes || BimiChecker.LogoProblems(svg) is { Count: > 0 })
        {
            return "The logo isn't SVG Tiny PS.";
        }

        var asset = await context.BimiAssets.SingleOrDefaultAsync(row => row.DomainId == domain.Id, cancellationToken).ConfigureAwait(false);
        var logo = Encoding.UTF8.GetString(svg);
        if (asset is null)
        {
            context.BimiAssets.Add(new BimiAsset { DomainId = domain.Id, LogoSvg = logo, UpdatedUtc = DateTimeOffset.UtcNow });
        }
        else
        {
            asset.LogoSvg = logo;
            asset.UpdatedUtc = DateTimeOffset.UtcNow;
        }

        AuditLog.Record(context, actor, AuditActions.BimiLogoSaved, AuditTarget.For(domain), $"Uploaded the BIMI logo for {domain.Name}");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>Saves a mark certificate (PEM) for a domain that already has a hosted logo. Returns why it was refused, or null.</summary>
    public static async Task<string?> SaveCertificateAsync(DotMarcDbContext context, AuditActor actor, Domain domain, byte[] pem, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var asset = await context.BimiAssets.SingleOrDefaultAsync(row => row.DomainId == domain.Id, cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            return "Upload the logo first.";
        }
        if (pem.Length > BimiChecker.MaxCertificateBytes || BimiChecker.InspectCertificate(pem, nowUtc).Problem is { } problem)
        {
            return $"That certificate can't be used: {BimiChecker.InspectCertificate(pem, nowUtc).Problem ?? "it's too large"}.";
        }

        asset.CertificatePem = Encoding.ASCII.GetString(pem);
        asset.UpdatedUtc = nowUtc;
        AuditLog.Record(context, actor, AuditActions.BimiCertificateSaved, AuditTarget.For(domain), $"Uploaded the BIMI mark certificate for {domain.Name}");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>Stops hosting the domain's BIMI files. Its record, if still published, then points at nothing.</summary>
    public static async Task RemoveAsync(DotMarcDbContext context, AuditActor actor, Domain domain, CancellationToken cancellationToken = default)
    {
        var asset = await context.BimiAssets.SingleOrDefaultAsync(row => row.DomainId == domain.Id, cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            return;
        }

        context.BimiAssets.Remove(asset);
        AuditLog.Record(context, actor, AuditActions.BimiRemoved, AuditTarget.For(domain), $"Stopped hosting the BIMI files for {domain.Name}");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

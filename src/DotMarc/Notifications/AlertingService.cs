using System.Globalization;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.IpEnrichment;
using DotMarc.Reporting;
using DotMarc.Senders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DotMarc.Notifications;

public interface IAlertingService
{
    Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default);
    Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default);
    Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default);
    Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default);
    Task RaiseClientReportFailedAsync(int groupId, string groupName, string periodLabel, string error, CancellationToken cancellationToken = default);
    Task ResolveClientReportFailedAsync(int groupId, CancellationToken cancellationToken = default);
    Task RaisePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, string reason, CancellationToken cancellationToken = default);
    Task ResolvePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, CancellationToken cancellationToken = default);
    Task RaiseBackupFailedAsync(string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task ResolveBackupFailedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class AlertingService : IAlertingService
{
    private readonly IDbContextFactory<DotMarcDbContext> _dbFactory;
    private readonly IAlertWebhookClient _alertWebhookClient;
    private readonly IPsaTicketService _psaTicketService;
    private readonly ILogger<AlertingService> _logger;
    private readonly IReverseDnsLookup? _reverseDnsLookup;

    public AlertingService(IDbContextFactory<DotMarcDbContext> dbFactory, IAlertWebhookClient alertWebhookClient, IPsaTicketService psaTicketService, ILogger<AlertingService> logger,
        IReverseDnsLookup? reverseDnsLookup = null)
    {
        _dbFactory = dbFactory;
        _alertWebhookClient = alertWebhookClient;
        _psaTicketService = psaTicketService;
        _logger = logger;
        _reverseDnsLookup = reverseDnsLookup;
    }

    /// <summary>How recent mail must be to count for UnapprovedSender, so approving a sender, or a sender going quiet,
    /// closes the alert within a week.</summary>
    public static readonly TimeSpan UnapprovedSenderWindow = TimeSpan.FromDays(7);

    /// <summary>Reverse DNS lookups one cycle may make to recognise senders; later cycles pick up the rest.</summary>
    private const int MaxSenderLookupsPerCycle = 50;

    public async Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Read live rather than once at startup: this is a singleton service, and settings are
        // now editable at any time from /alerts/settings (see NotificationSettings's doc
        // comment) - a value bound once via IOptions would never pick up a later change without
        // a restart.
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-settings.MissingReportThresholdDays);
        var reasonWindowCutoffUtc = DomainStatistics.GetWindowCutoffUtc();
        var domains = await db.Domains
            .AsNoTracking()
            .Where(d => d.IsMonitored)
            .Include(d => d.Reports.Where(r => r.ReceivedUtc >= reasonWindowCutoffUtc))
            .ThenInclude(r => r.Records)
            .ThenInclude(rec => rec.OverrideReasons)
            .Include(d => d.Reports.Where(r => r.ReceivedUtc >= reasonWindowCutoffUtc))
            .ThenInclude(r => r.Records)
            .ThenInclude(rec => rec.AuthDetails)
            // Sibling collections under each record: split so they aren't joined into one
            // row-multiplying result that repeats every report's RawXml.
            .AsSplitQuery()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var domain in domains)
        {
            if (domain.SpfCheckStatus == SpfCheckStatus.NullSpf)
            {
                // Null-routed (SPF v=spf1 -all): no reports is the expected, healthy state, not a
                // problem - resolve any pre-existing alert from before the domain became
                // null-routed and skip the missing-report check entirely for it.
                await ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);

                // UnexpectedActivityOnNullRoutedDomain resolves only once the domain has gone
                // quiet again (no report within the missing-report threshold window). This branch
                // runs unconditionally for every null-routed domain each cycle, so resolving it
                // unconditionally here would auto-close the alert on the very next poll after it
                // fires, defeating its purpose of surfacing unexpected activity for an admin to
                // see. Same threshold semantics as MissedReport, just inverted: fires when a
                // report unexpectedly arrives, stays open while reports keep arriving within the
                // threshold window, auto-resolves once activity stops for the threshold period.
                if (domain.LastReportReceivedUtc is null || domain.LastReportReceivedUtc < cutoffUtc)
                {
                    await ResolveAlertAsync(domain.Name, AlertTypes.UnexpectedActivityOnNullRoutedDomain, cancellationToken).ConfigureAwait(false);
                }
            }
            else if (domain.LastReportReceivedUtc is { } lastReport && lastReport >= cutoffUtc)
            {
                await ResolveDomainAlertAsync(domain.Name, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var message = domain.LastReportReceivedUtc is { } receivedUtc
                    ? $"The monitored domain '{domain.Name}' has not received a DMARC report since {receivedUtc:O}."
                    : $"The monitored domain '{domain.Name}' has not received a DMARC report yet.";
                await EnsureAlertAsync(db, settings, domain.Name, AlertTypes.MissedReport, "Warning", "Missing expected DMARC report", message, cancellationToken).ConfigureAwait(false);
            }

            // Independent of the report-freshness branch above - a domain can be reporting fine
            // AND have a reject mix worth flagging, so this always runs.
            await CheckSuspiciousRejectActivityAsync(db, settings, domain, cancellationToken).ConfigureAwait(false);
        }

        await CheckUnapprovedSendersAsync(db, settings, domains, cancellationToken).ConfigureAwait(false);

        await CheckDnsHealthAsync(db, settings, domains, cancellationToken).ConfigureAwait(false);
        await CheckApiKeyExpiryAsync(db, settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs DnsHealthAlertEvaluator for every monitored domain, saves what it remembers, then raises and
    /// resolves alerts. Resolves are only sent for alerts that are open, so a quiet cycle costs one query, not one per
    /// domain per check.</summary>
    private async Task CheckDnsHealthAsync(DotMarcDbContext db, NotificationSettings settings, List<Domain> domains, CancellationToken cancellationToken)
    {
        await AutoCloseAcknowledgeableAlertsAsync(settings, cancellationToken).ConfigureAwait(false);

        var nowUtc = DateTimeOffset.UtcNow;
        var domainIds = domains.Select(domain => domain.Id).ToList();
        var states = await db.DomainAlertStates
            .Where(state => domainIds.Contains(state.DomainId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var openAlerts = (await db.AlertEvents
                .Where(alert => !alert.IsResolved && AlertTypes.DnsHealth.Contains(alert.AlertType))
                .Select(alert => new { alert.DomainName, alert.AlertType })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(alert => (alert.DomainName, alert.AlertType))
            .ToHashSet();

        var actionsByDomain = new List<(Domain Domain, IReadOnlyList<DnsHealthAlertAction> Actions)>();
        foreach (var domain in domains)
        {
            var domainStates = states.Where(state => state.DomainId == domain.Id).ToList();
            actionsByDomain.Add((domain, DnsHealthAlertEvaluator.Evaluate(domain, domainStates, settings, nowUtc)));
            db.DomainAlertStates.AddRange(domainStates.Where(state => state.Id == 0));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (domain, actions) in actionsByDomain)
        {
            foreach (var action in actions)
            {
                if (action.Kind == DnsHealthActionKind.Raise)
                {
                    if (AlertAcknowledgement.IsAcknowledgeable(action.AlertType)
                        && !await IsStillPendingAsync(db, domain.Id, action.AlertType, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await EnsureAlertAsync(db, settings, domain.Name, action.AlertType, action.Severity, action.Title, action.Message, cancellationToken).ConfigureAwait(false);
                }
                else if (openAlerts.Contains((domain.Name, action.AlertType)))
                {
                    await ResolveAllCopiesAsync(domain.Name, action.AlertType, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // A domain that stops being monitored isn't evaluated any more, so close what it left open.
        var monitoredNames = domains.Select(domain => domain.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (domainName, alertType) in openAlerts.Where(alert => !monitoredNames.Contains(alert.DomainName)))
        {
            await ResolveAllCopiesAsync(domainName, alertType, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CheckSuspiciousRejectActivityAsync(DotMarcDbContext context, NotificationSettings settings, Domain domain, CancellationToken cancellationToken)
    {
        var breakdown = DomainStatistics.GetReasonBreakdown(domain.Reports);
        var nonBenign = breakdown.LocalPolicy + breakdown.Other + breakdown.InferredSpfFailure + breakdown.InferredDkimFailure + breakdown.InferredBothFailure + breakdown.NoReasonGiven;
        var nonBenignPercent = breakdown.Total == 0 ? 0 : (double)nonBenign / breakdown.Total * 100;

        if (breakdown.Total >= settings.SuspiciousRejectMinVolume && nonBenignPercent >= settings.SuspiciousRejectNonBenignPercent)
        {
            var message = $"'{domain.Name}' rejected/quarantined {breakdown.Total} message(s) in the last 30 days, and {nonBenignPercent:F0}% of those had no benign override reason (forwarder/mailing list/sampling) - this looks like more than benign forwarding." +
                BuildInferredBreakdownClause(breakdown);
            await EnsureAlertAsync(context, settings, domain.Name, AlertTypes.SuspiciousRejectActivity, "Warning", "Reject activity looks like more than benign forwarding", message, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ResolveAlertAsync(domain.Name, AlertTypes.SuspiciousRejectActivity, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Raises UnapprovedSender for a domain with at least one approved sender (so someone has reviewed its
    /// senders) when, in the last week, mail passing DMARC came from a sender not approved: one nobody reviewed, or one
    /// marked not ours, which shouldn't be able to pass at all. Mail failing DMARC is left out: that's spoofing DMARC is
    /// already dealing with, and it stays visible, unreviewed, on the Sources tab.</summary>
    private async Task CheckUnapprovedSendersAsync(DotMarcDbContext db, NotificationSettings settings, List<Domain> domains, CancellationToken cancellationToken)
    {
        var domainIds = domains.Select(domain => domain.Id).ToList();
        var approvals = await db.SenderApprovals
            .AsNoTracking()
            .Where(approval => domainIds.Contains(approval.DomainId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var approvalsByDomain = approvals.ToLookup(approval => approval.DomainId);
        var windowStartUtc = DateTimeOffset.UtcNow - UnapprovedSenderWindow;

        var lookupsLeft = MaxSenderLookupsPerCycle;
        foreach (var domain in domains)
        {
            var decisions = approvalsByDomain[domain.Id].ToDictionary(approval => approval.SenderKey, approval => approval.Status);
            var volumeByIp = domain.Reports
                .Where(report => report.ReceivedUtc >= windowStartUtc)
                .SelectMany(report => report.Records)
                .Where(DomainStatistics.IsPassing)
                .GroupBy(record => record.SourceIp)
                .ToDictionary(group => group.Key, group => group.Sum(record => record.MessageCount));
            if (!decisions.ContainsValue(SenderApprovalStatus.Approved) || volumeByIp.Count == 0)
            {
                await ResolveAlertAsync(domain.Name, AlertTypes.UnapprovedSender, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var ips = volumeByIp.Keys.ToList();
            var hostNames = await IpHostNameService.GetCachedAsync(db, ips, cancellationToken).ConfigureAwait(false);
            var owners = await IpInfoService.GetCachedAsync(db, ips, cancellationToken).ConfigureAwait(false);
            var unapproved = new Dictionary<string, (string Label, int Volume, bool MarkedNotOurs)>();
            foreach (var (ip, volume) in volumeByIp)
            {
                hostNames.TryGetValue(ip, out var cachedName);
                var hostName = cachedName?.HostName;
                if (_reverseDnsLookup is not null && lookupsLeft > 0 && IpHostNameService.NeedsLookup(cachedName, DateTimeOffset.UtcNow))
                {
                    lookupsLeft--;
                    hostName = (await IpHostNameService.EnrichAsync(_dbFactory, _reverseDnsLookup, ip, cancellationToken).ConfigureAwait(false)).HostName;
                }

                owners.TryGetValue(ip, out var owner);
                var identity = SenderIdentity.For(ip, hostName, owner?.Organization);
                var decided = decisions.TryGetValue(identity.Key, out var status);
                if ((decided && status == SenderApprovalStatus.Approved) || (!decided && identity.IsForwarder))
                {
                    continue;
                }

                var previous = unapproved.GetValueOrDefault(identity.Key, (Label: identity.Label, Volume: 0, MarkedNotOurs: status == SenderApprovalStatus.NotOurs));
                unapproved[identity.Key] = (previous.Label, previous.Volume + volume, previous.MarkedNotOurs);
            }

            if (unapproved.Count == 0)
            {
                await ResolveAlertAsync(domain.Name, AlertTypes.UnapprovedSender, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var listed = unapproved.Values.OrderByDescending(sender => sender.Volume).ToList();
            var described = string.Join(", ", listed.Take(5).Select(sender =>
                $"{sender.Label} ({sender.Volume} message{(sender.Volume == 1 ? "" : "s")}{(sender.MarkedNotOurs ? ", marked not ours" : "")})"));
            var more = listed.Count > 5 ? $" and {listed.Count - 5} more" : "";
            var message = $"In the last 7 days, mail from '{domain.Name}' passed DMARC from {listed.Count} sender(s) nobody approved: {described}{more}. " +
                "Approve them on the domain's Sources tab if they're the domain's own mail; otherwise find out who added them to its SPF or DKIM.";
            await EnsureAlertAsync(db, settings, domain.Name, AlertTypes.UnapprovedSender, "Warning", "Mail passing DMARC from an unapproved sender", message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Appends a short "of that, X had inferred SPF/DKIM issues" clause when any of the
    /// three inferred buckets are non-zero, so the alert itself points at which mechanism to
    /// investigate first instead of sending the reader to the dashboard for that.</summary>
    private static string BuildInferredBreakdownClause(ReasonBreakdown breakdown)
    {
        var parts = new List<string>();
        if (breakdown.InferredSpfFailure > 0)
        {
            parts.Add($"{breakdown.InferredSpfFailure} an inferred SPF-only failure");
        }
        if (breakdown.InferredDkimFailure > 0)
        {
            parts.Add($"{breakdown.InferredDkimFailure} an inferred DKIM-only failure");
        }
        if (breakdown.InferredBothFailure > 0)
        {
            parts.Add($"{breakdown.InferredBothFailure} both SPF and DKIM failing or misaligned");
        }

        return parts.Count == 0 ? "" : $" Of that: {string.Join(", ", parts)}.";
    }

    public async Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default)
        => await ResolveAlertAsync(domainName, AlertTypes.MissedReport, cancellationToken).ConfigureAwait(false);

    public async Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default)
    {
        if (failedSessionCount == 0)
        {
            await ResolveAlertAsync(domainName, AlertTypes.TlsrptFailure, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var failureSummary = failureTypes.Count == 0 ? "no failure category supplied" : string.Join(", ", failureTypes.Distinct(StringComparer.OrdinalIgnoreCase));
        await EnsureAlertAsync(db, settings, domainName, AlertTypes.TlsrptFailure, "Warning", "TLS delivery failures reported", $"TLSRPT reported {failedSessionCount} failed TLS delivery session(s) for '{domainName}'. Failure types: {failureSummary}.", cancellationToken).ConfigureAwait(false);
    }

    public async Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        // Benign-dominant (at least half the reject/quarantine volume has a forwarder/mailing-list/
        // sampling override reason) reads as "probably not an attack"; anything else, including no
        // reject/quarantine volume at all yet, adds no extra sentence rather than guessing.
        var reasonContext = reasonBreakdown.Total == 0
            ? ""
            : reasonBreakdown.BenignOverride * 2 >= reasonBreakdown.Total
                ? " This looks like a forwarder or mailing list, not spoofing."
                : " No benign override reason was given - this looks like a genuine spoofing attempt.";

        var message = $"'{domainName}' is marked null-routed (SPF v=spf1 -all - no authorized senders) but a DMARC aggregate report just arrived showing mail activity. This may be legitimate traffic that needs accounting for, or a spoofing attempt.{reasonContext}";
        await EnsureAlertAsync(db, settings, domainName, AlertTypes.UnexpectedActivityOnNullRoutedDomain, "Warning", "Unexpected mail activity on a null-routed domain", message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a policy or nameserver change is still waiting, read fresh from the database. Someone may have
    /// acknowledged it (or closed its ticket) since this cycle read the state; raising it anyway would send a new
    /// notification and ticket seconds after they dismissed it.</summary>
    private static Task<bool> IsStillPendingAsync(DotMarcDbContext db, int domainId, string alertType, CancellationToken cancellationToken)
    {
        var item = alertType == AlertTypes.DmarcPolicyWeakened ? DnsHealthItems.DmarcPolicy : DnsHealthItems.Nameservers;
        return db.DomainAlertStates
            .AsNoTracking()
            .AnyAsync(state => state.DomainId == domainId && state.Item == item && state.PendingSinceUtc != null, cancellationToken);
    }

    /// <summary>Resolves every open copy of a DNS health alert. One left open past the cooldown is raised again as a new
    /// row with its own ticket, so after a long outage there can be several; they all close once the condition clears,
    /// not one per cycle.</summary>
    private async Task ResolveAllCopiesAsync(string domainName, string alertType, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var openCopies = await db.AlertEvents
            .Where(e => e.DomainName == domainName && e.AlertType == alertType && !e.IsResolved)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var resolvedUtc = DateTimeOffset.UtcNow;
        foreach (var copy in openCopies)
        {
            copy.IsResolved = true;
            copy.ResolvedUtc = resolvedUtc;
            try
            {
                await _psaTicketService.CloseTicketsAsync(db, copy, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to close PSA ticket for {DomainName} alert {AlertType}.", copy.DomainName, copy.AlertType);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ResolveAlertAsync(string domainName, string alertType, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var activeAlert = await db.AlertEvents
            .Where(e => e.DomainName == domainName && e.AlertType == alertType && !e.IsResolved)
            .OrderByDescending(e => e.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeAlert is null)
        {
            return;
        }

        activeAlert.IsResolved = true;
        activeAlert.ResolvedUtc = DateTimeOffset.UtcNow;

        try
        {
            await _psaTicketService.CloseTicketsAsync(db, activeAlert, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close PSA ticket for {DomainName} alert {AlertType}.", activeAlert.DomainName, activeAlert.AlertType);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Raises an alert unless the same one is open and younger than the cooldown, which is the settings' cooldown
    /// unless <paramref name="cooldown"/> overrides it.</summary>
    private async Task EnsureAlertAsync(DotMarcDbContext context, NotificationSettings settings, string domainName, string alertType, string severity, string title, string message, CancellationToken cancellationToken, TimeSpan? cooldown = null)
    {
        if (AlertTypes.Find(alertType) is null)
        {
            // A developer slip, not something to stop the cycle over: the alert is still raised (and creates a ticket,
            // the default), it just isn't listed on the ticket rule screens until it is added to AlertTypes.All.
            _logger.LogError("Alert type {AlertType} is not in AlertTypes.All, so it can't be controlled from the ticket rule screens. Add it there. The alert is still raised.", alertType);
        }

        var activeAlert = await context.AlertEvents
            .Where(e => e.DomainName == domainName && e.AlertType == alertType && !e.IsResolved)
            .OrderByDescending(e => e.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeAlert is not null)
        {
            if (activeAlert.CreatedUtc > DateTimeOffset.UtcNow.Subtract(cooldown ?? TimeSpan.FromMinutes(settings.CooldownMinutes)))
            {
                return;
            }
        }

        var alert = new AlertEvent
        {
            DomainName = domainName,
            AlertType = alertType,
            Severity = severity,
            Title = title,
            Message = message,
            CreatedUtc = DateTimeOffset.UtcNow
        };

        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _alertWebhookClient.SendAlertAsync(settings, domainName, alertType, title, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send notification for {DomainName} alert {AlertType}.", domainName, alertType);
        }

        try
        {
            await _psaTicketService.CreateTicketsAsync(context, alert, cancellationToken).ConfigureAwait(false);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create PSA ticket for {DomainName} alert {AlertType}.", domainName, alertType);
        }
    }

    /// <summary>What a Group's failed report alert is about. Stored where a domain alert stores its domain name; it ends
    /// with the Group's id so the alert still resolves after the Group is renamed.</summary>
    public static string ClientReportAlertSubject(int groupId, string groupName) =>
        string.Create(CultureInfo.InvariantCulture, $"Client report for {groupName} (group {groupId})");

    private static string ClientReportSubjectSuffix(int groupId) => string.Create(CultureInfo.InvariantCulture, $"(group {groupId})");

    public async Task RaiseClientReportFailedAsync(int groupId, string groupName, string periodLabel, string error, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        await EnsureAlertAsync(db, settings, ClientReportAlertSubject(groupId, groupName), AlertTypes.ClientReportFailed, "Warning", "Client report failed",
            $"The {periodLabel} report for {groupName} couldn't be sent for 24 hours. Last error: {error}", cancellationToken).ConfigureAwait(false);
    }

    public async Task ResolveClientReportFailedAsync(int groupId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var suffix = ClientReportSubjectSuffix(groupId);
        var subjects = await db.AlertEvents
            .Where(alert => alert.AlertType == AlertTypes.ClientReportFailed && !alert.IsResolved && alert.DomainName.EndsWith(suffix))
            .Select(alert => alert.DomainName)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var subject in subjects)
        {
            await ResolveAllCopiesAsync(subject, AlertTypes.ClientReportFailed, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What a PSA's lost-connection alert is about. Stored where a domain alert stores its domain name.</summary>
    public static string PsaConnectionAlertSubject(DotMarc.Psa.PsaKind psa) => $"{DotMarc.Psa.PsaKindNames.DisplayName(psa)} connection";

    public async Task RaisePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, string reason, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        var name = DotMarc.Psa.PsaKindNames.DisplayName(psa);
        await EnsureAlertAsync(db, settings, PsaConnectionAlertSubject(psa), AlertTypes.PsaConnectionLost, "Warning", "PSA connection lost",
            $"dotMARC can no longer refresh its {name} sign-in ({reason}). New alerts won't raise {name} tickets until someone reconnects under Manage > PSA settings > {name}.",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ResolvePsaConnectionLostAsync(DotMarc.Psa.PsaKind psa, CancellationToken cancellationToken = default)
    {
        var subject = PsaConnectionAlertSubject(psa);
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            // Called every keep-alive run, so a quiet run costs one query.
            if (!await db.AlertEvents.AnyAsync(alert => alert.AlertType == AlertTypes.PsaConnectionLost && alert.DomainName == subject && !alert.IsResolved, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        await ResolveAllCopiesAsync(subject, AlertTypes.PsaConnectionLost, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What a failed backup's alert is about, stored where a domain alert stores its domain name.</summary>
    public const string BackupAlertSubject = "Offsite backups";

    public async Task RaiseBackupFailedAsync(string reason, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await NotificationSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            return;
        }

        await EnsureAlertAsync(db, settings, BackupAlertSubject, AlertTypes.BackupFailed, "Warning", "Offsite backup failed",
            $"dotMARC's offsite backup failed: {reason} It's retried hourly. See Manage > Control panel > Backups.", cancellationToken).ConfigureAwait(false);
    }

    public async Task ResolveBackupFailedAsync(CancellationToken cancellationToken = default)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await db.AlertEvents.AnyAsync(alert => alert.AlertType == AlertTypes.BackupFailed && alert.DomainName == BackupAlertSubject && !alert.IsResolved, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        await ResolveAllCopiesAsync(BackupAlertSubject, AlertTypes.BackupFailed, cancellationToken).ConfigureAwait(false);
    }

    public static readonly TimeSpan ApiKeyExpiryWarning = TimeSpan.FromDays(14);

    /// <summary>What an expiring key's alert is about. Stored where a domain alert stores its domain name; a key's name
    /// and prefix never change, so the same key always has the same subject.</summary>
    public static string ApiKeyAlertSubject(ApiKey key) => $"API key {key.Name} ({key.Prefix})";

    /// <summary>Warns before an API key expires so whatever uses it doesn't break unannounced. An expiry is a known date,
    /// so it's announced once (the cooldown is the whole warning period) rather than every cooldown like a fault. The
    /// alert closes when the key is revoked or finally expires.</summary>
    private async Task CheckApiKeyExpiryAsync(DotMarcDbContext db, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var openSubjects = (await db.AlertEvents
            .Where(alert => alert.AlertType == AlertTypes.ApiKeyExpiring && !alert.IsResolved)
            .Select(alert => alert.DomainName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet();
        var keys = await db.ApiKeys.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var apiKey in keys)
        {
            var subject = ApiKeyAlertSubject(apiKey);
            var expiresSoon = apiKey.IsActive(nowUtc) && apiKey.ExpiresUtc - nowUtc <= ApiKeyExpiryWarning;
            if (expiresSoon)
            {
                await EnsureAlertAsync(db, settings, subject, AlertTypes.ApiKeyExpiring, "Warning", "API key expiring soon",
                    $"The API key '{apiKey.Name}' ({apiKey.Prefix}...) expires on {DotMarc.Reporting.UkTime.Date(apiKey.ExpiresUtc)}. Create a replacement on the Access page, switch whatever uses this key over to it, then revoke this one.",
                    cancellationToken, cooldown: ApiKeyExpiryWarning).ConfigureAwait(false);
            }
            else if (openSubjects.Contains(subject))
            {
                await ResolveAllCopiesAsync(subject, AlertTypes.ApiKeyExpiring, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static readonly AuditActor AutoCloseActor = AuditActor.ForSystem("Alert auto-close");

    /// <summary>With AcknowledgeableAutoCloseDays above 0, closes policy and nameserver alerts left open that long, as
    /// if acknowledged.</summary>
    private async Task AutoCloseAcknowledgeableAlertsAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        if (settings.AcknowledgeableAutoCloseDays <= 0)
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-settings.AcknowledgeableAutoCloseDays);
        var staleAlertIds = await db.AlertEvents
            .Where(alert => !alert.IsResolved && alert.CreatedUtc < cutoffUtc
                && (alert.AlertType == AlertTypes.DmarcPolicyWeakened || alert.AlertType == AlertTypes.NameserversChanged))
            .Select(alert => alert.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var alertId in staleAlertIds)
        {
            await using var alertContext = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var result = await AlertAcknowledgement.AcknowledgeAsync(alertContext, AutoCloseActor, alertId, _psaTicketService, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == AcknowledgeOutcome.AcknowledgedButTicketNotClosed)
            {
                _logger.LogWarning("Closed alert {AlertId} automatically, but couldn't close all its PSA tickets. dotMARC will keep trying.", alertId);
            }
        }
    }
}

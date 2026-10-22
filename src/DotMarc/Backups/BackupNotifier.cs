using System.Net;
using System.Text;
using DotMarc.Email;
using DotMarc.Reporting;

namespace DotMarc.Backups;

/// <summary>What one backup run did, for its email: the figures when it worked, the step and error when it didn't.</summary>
public sealed record BackupRunReport(
    bool Succeeded,
    string Trigger,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    string? Destination,
    string? Key,
    long? DatabaseBytes,
    long? EncryptedBytes,
    int Pruned,
    int? StoredCount,
    long? StoredBytes,
    DateTimeOffset? OldestStoredUtc,
    string? FailedStep,
    string? Error,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? NextRunUtc)
{
    public TimeSpan Duration => FinishedUtc - StartedUtc;
}

/// <summary>Emails a backup's result to the settings' recipients. A failure is always sent: on the first failure, then at
/// most every FailureRepeat while it keeps failing (the scheduler retries hourly). A success is sent when NotifyOnSuccess
/// is on, and always when it follows a failure. A failed send is logged, never surfaced: the backup's own result stands.</summary>
public sealed class BackupNotifier(IEmailSenderFactory senderFactory, IConfiguration configuration, ILogger<BackupNotifier> logger)
{
    public static readonly TimeSpan FailureRepeat = TimeSpan.FromHours(6);

    /// <summary>Whether to email this run, given the settings as they were before it.</summary>
    public static bool ShouldSend(BackupSettings before, BackupRunReport report)
    {
        if (before.NotifyRecipients.Count == 0)
        {
            return false;
        }
        if (report.Succeeded)
        {
            return before.NotifyOnSuccess || before.LastRunSucceeded == false;
        }

        return before.LastRunSucceeded != false || before.LastFailureEmailUtc is not { } last || report.FinishedUtc - last >= FailureRepeat;
    }

    /// <summary>Sends the email; true when it went.</summary>
    public async Task<bool> SendAsync(BackupSettings before, BackupRunReport report, CancellationToken cancellationToken)
    {
        try
        {
            var sender = await senderFactory.GetAsync(cancellationToken).ConfigureAwait(false);
            if (sender is null)
            {
                logger.LogInformation("Backup email not sent: email is off.");
                return false;
            }

            await sender.SendAsync(Compose(before.NotifyRecipients, report, before.LastRunSucceeded == false, configuration["MtaSts:HostingHostname"]), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Couldn't email the backup result.");
            return false;
        }
    }

    public static EmailMessage Compose(IReadOnlyList<string> to, BackupRunReport report, bool recovered, string? hostname)
    {
        var server = string.IsNullOrWhiteSpace(hostname) ? Environment.MachineName : hostname;
        var subject = report.Succeeded
            ? $"{(recovered ? "Backups working again" : "Backup succeeded")}: dotMARC {server}, {UkTime.DateTime(report.FinishedUtc)}"
            : $"Backup FAILED: dotMARC {server}, {UkTime.DateTime(report.FinishedUtc)}";

        var rows = new List<(string Label, string Value)>
        {
            ("Result", report.Succeeded ? (recovered ? "Succeeded (the previous backup had failed)" : "Succeeded") : "Failed"),
            ("Started", $"{UkTime.DateTimeSeconds(report.StartedUtc)} ({report.Trigger})"),
            ("Took", Duration(report.Duration)),
        };
        if (report.Succeeded)
        {
            rows.Add(("Database", report.DatabaseBytes is { } database ? OffsiteBackupService.FormatSize(database) : "Unknown"));
            rows.Add(("Backup file (compressed, encrypted)", report.EncryptedBytes is { } encrypted ? OffsiteBackupService.FormatSize(encrypted) : "Unknown"));
            rows.Add(("Saved to", $"{report.Destination}/{report.Key?[(report.Key.LastIndexOf('/') + 1)..]}"));
            rows.Add(("Older backups removed", report.Pruned == 0 ? "None" : report.Pruned.ToString()));
            if (report.StoredCount is { } count)
            {
                rows.Add(("Backups stored", $"{count}, {OffsiteBackupService.FormatSize(report.StoredBytes ?? 0)} in all" +
                    (report.OldestStoredUtc is { } oldest ? $", oldest {UkTime.Date(oldest)}" : "")));
            }
        }
        else
        {
            rows.Add(("Failed while", report.FailedStep ?? "starting"));
            rows.Add(("Error", report.Error ?? "Unknown"));
            rows.Add(("Last good backup", report.LastSuccessUtc is { } success ? UkTime.DateTime(success) : "None yet"));
        }
        rows.Add(("Next scheduled backup", report.NextRunUtc is { } next ? UkTime.DateTime(next) : "Scheduled backups are off"));
        rows.Add(("Server", server));

        var lead = report.Succeeded
            ? "The offsite backup of dotMARC worked."
            : "The offsite backup of dotMARC failed. dotMARC tries again every hour; this email repeats at most every 6 hours while it keeps failing.";
        var html = new StringBuilder()
            .Append("<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#222\">")
            .Append($"<p style=\"font-weight:bold;color:{(report.Succeeded ? "#1b7f3b" : "#b3261e")}\">{E(lead)}</p>")
            .Append("<table cellpadding=\"6\" style=\"border-collapse:collapse\">");
        foreach (var (label, value) in rows)
        {
            html.Append($"<tr><td style=\"color:#555;vertical-align:top\">{E(label)}</td><td>{E(value)}</td></tr>");
        }
        html.Append("</table>");
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            html.Append($"<p><a href=\"https://{E(hostname)}/control-panel/backups\">Open the Backups page</a></p>");
        }
        html.Append("</div>");

        var text = new StringBuilder(lead).AppendLine().AppendLine();
        foreach (var (label, value) in rows)
        {
            text.AppendLine($"{label}: {value}");
        }
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            text.AppendLine().AppendLine($"Backups page: https://{hostname}/control-panel/backups");
        }

        return new EmailMessage(to, subject, html.ToString(), text.ToString(), []);

        static string E(string value) => WebUtility.HtmlEncode(value);
    }

    private static string Duration(TimeSpan duration) =>
        duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes} min {duration.Seconds} s" : $"{Math.Max(0, duration.TotalSeconds):0.0} s";
}

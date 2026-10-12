using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Email;
using DotMarc.Reporting.ClientReports;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class ClientReportEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private readonly RecordingSender _sender = new();
    private ApiTestHost _host = null!;

    public ClientReportEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    // The demo host has no email; give it a sender that records what it's asked to send.
    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture,
        configureServices: services => services.AddSingleton<IEmailSenderFactory>(new FixedSenderFactory(_sender)));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedSenderFactory(IEmailSender? sender) : IEmailSenderFactory
    {
        public Task<IEmailSender?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(sender);
    }

    private async Task<int> SeedScheduledGroupAsync(string name, params string[] recipients)
    {
        var groupId = await _host.SeedGroupAsync(name);
        await using var context = _host.CreateContext();
        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, groupId, ReportFrequency.Monthly, recipients);
        return groupId;
    }

    [Fact]
    public async Task TheSchedule_AndItsNextRun()
    {
        var groupId = await SeedScheduledGroupAsync("api-reports-schedule", "it@contoso.example");
        var unscheduled = await _host.SeedGroupAsync("api-reports-none");
        var (_, secret) = await _host.CreateKeyAsync([Permission.ReportsManage]);
        using var client = _host.ClientFor(secret);

        var schedule = await client.GetFromJsonAsync<ApiReportSchedule>($"/api/v1/groups/{groupId}/report-schedule");
        var none = await client.GetFromJsonAsync<ApiReportSchedule>($"/api/v1/groups/{unscheduled}/report-schedule");

        Assert.Equal("Monthly", schedule!.Frequency);
        Assert.Equal(["it@contoso.example"], schedule.Recipients);
        Assert.Equal("Month", schedule.NextPeriod!.Kind);
        Assert.NotNull(schedule.NextDueUtc);
        Assert.Equal(("Off", 0), (none!.Frequency, none.Recipients.Count));
        Assert.Null(none.NextPeriod);
    }

    [Fact]
    public async Task ThePdf_ForAMonth_AndPeriodRulesRefused()
    {
        var groupId = await SeedScheduledGroupAsync("api-reports-pdf", "it@contoso.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.ReportsManage]);
        using var client = _host.ClientFor(secret);

        var pdf = await client.GetAsync($"/api/v1/groups/{groupId}/report?period=month&of=2026-09-15");
        var both = await client.GetAsync($"/api/v1/groups/{groupId}/report?period=month&start=2026-09-01&end=2026-09-30");
        var half = await client.GetAsync($"/api/v1/groups/{groupId}/report?start=2026-09-01");

        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Contains("September 2026", pdf.Content.Headers.ContentDisposition?.FileNameStar ?? pdf.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("%PDF-"u8.ToArray(), (await pdf.Content.ReadAsByteArrayAsync())[..5]);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, half.StatusCode);
    }

    [Fact]
    public async Task SendNow_SendsRecordsAndAudits_AndShowsInTheHistory()
    {
        var groupId = await SeedScheduledGroupAsync("api-reports-send", "it@contoso.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.ReportsManage], name: "reports key");
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync($"/api/v1/groups/{groupId}/report/send", new { period = "month", of = "2026-09-15" });
        var history = await client.GetFromJsonAsync<ApiPage<ApiReportDelivery>>($"/api/v1/groups/{groupId}/report-deliveries?status=sent&kind=Manual");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delivery = (await response.Content.ReadFromJsonAsync<ApiReportDelivery>())!;
        Assert.Equal(("Sent", "Manual"), (delivery.Status, delivery.Kind));
        Assert.Equal(["it@contoso.example"], delivery.Recipients);
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), (delivery.PeriodStart, delivery.PeriodEnd));
        Assert.Single(_sender.Sent);
        Assert.Equal([delivery.Id], history!.Items.Select(item => item.Id));
        await using var context = _host.CreateContext();
        Assert.Contains(await context.AuditEntries.Where(entry => entry.Action == AuditActions.ClientReportSent).ToListAsync(),
            entry => entry.ActorKind == AuditActorKind.ApiKey && entry.ActorName.Contains("reports key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendNow_RefusesBadRecipients_AndNoRecipients_WithoutSending()
    {
        var groupId = await SeedScheduledGroupAsync("api-reports-bad", "it@contoso.example");
        var empty = await _host.SeedGroupAsync("api-reports-empty");
        var (_, secret) = await _host.CreateKeyAsync([Permission.ReportsManage]);
        using var client = _host.ClientFor(secret);

        var bad = await client.PostAsJsonAsync($"/api/v1/groups/{groupId}/report/send", new { period = "month", recipients = new[] { "not an address" } });
        var none = await client.PostAsJsonAsync($"/api/v1/groups/{empty}/report/send", new { period = "month" });

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("\"recipients\"", await bad.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Empty(_sender.Sent);
        await using var context = _host.CreateContext();
        Assert.Empty(await context.ClientReportDeliveries.Where(delivery => delivery.GroupId == groupId || delivery.GroupId == empty).ToListAsync());
    }

    [Fact]
    public async Task AGroupLimitedKey_GetsA404ForAnotherGroup_AndNothingIsSent()
    {
        var own = await SeedScheduledGroupAsync("api-reports-own", "own@contoso.example");
        var other = await SeedScheduledGroupAsync("api-reports-other", "other@fabrikam.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.ReportsManage], scopedGroupIds: [own]);
        using var client = _host.ClientFor(secret);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/groups/{other}/report-schedule")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/groups/{other}/report-deliveries")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/groups/{other}/report?period=month")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/v1/groups/{other}/report/send", new { period = "month" })).StatusCode);
        Assert.Empty(_sender.Sent);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/groups/{own}/report-schedule")).StatusCode);
    }
}

[Collection("Postgres")]
public sealed class ClientReportEmailOffTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public ClientReportEmailOffTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task SendNow_WithEmailOff_IsA409()
    {
        var groupId = await _host.SeedGroupAsync("api-reports-off");
        await using (var context = _host.CreateContext())
        {
            await ClientReportService.SetScheduleAsync(context, TestActors.Admin, groupId, ReportFrequency.Monthly, ["it@contoso.example"]);
        }

        var (_, secret) = await _host.CreateKeyAsync([Permission.ReportsManage]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync($"/api/v1/groups/{groupId}/report/send", new { period = "month" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}

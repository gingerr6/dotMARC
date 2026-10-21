using DotMarc.Data;
using DotMarc.Setup;
using Xunit;

namespace DotMarc.Tests.Setup;

public sealed class DomainChecklistTests
{
    private static Domain AllGood() => new()
    {
        Name = "contoso.com",
        FirstSeenUtc = DateTimeOffset.UtcNow,
        DmarcCheckStatus = DmarcCheckStatus.Ok,
        DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.NotApplicable,
        SpfCheckStatus = SpfCheckStatus.Ok,
        DkimCheckStatus = DkimCheckStatus.Ok,
        TlsrptCheckStatus = TlsrptCheckStatus.Ok,
        MtaStsEnabled = true,
        MtaStsStatus = MtaStsStatus.Active,
    };

    [Fact]
    public void ADomainWithEveryRecord_IsDone()
    {
        var items = DomainChecklist.For(AllGood());

        Assert.Equal(["dmarc", "spf", "dkim", "tlsrpt", "mta-sts"], items.Select(item => item.Key));
        Assert.All(items, item => Assert.Equal(ChecklistState.Done, item.State));
        Assert.Equal((5, 5), DomainChecklist.Progress(items));
    }

    [Fact]
    public void MissingRecords_AreToDo_WithTheRecordToAdd()
    {
        var domain = AllGood();
        domain.DmarcCheckStatus = DmarcCheckStatus.MissingOwnRecord;
        domain.TlsrptCheckStatus = TlsrptCheckStatus.Misconfigured;

        var items = DomainChecklist.For(domain).ToDictionary(item => item.Key);

        Assert.Equal((ChecklistState.ToDo, "dmarc"), (items["dmarc"].State, items["dmarc"].ChangeTarget));
        Assert.Equal((ChecklistState.ToDo, "tlsrpt"), (items["tlsrpt"].State, items["tlsrpt"].ChangeTarget));
        Assert.Equal((3, 5), DomainChecklist.Progress(DomainChecklist.For(domain)));
    }

    [Fact]
    public void MtaStsAndDkim_DontCount_UntilTheyreSetUp()
    {
        var domain = AllGood();
        domain.MtaStsEnabled = false;
        domain.DkimCheckStatus = DkimCheckStatus.NotConfigured;

        var items = DomainChecklist.For(domain).ToDictionary(item => item.Key);

        Assert.Equal(ChecklistState.NotStarted, items["mta-sts"].State);
        Assert.Equal(ChecklistState.NotStarted, items["dkim"].State);
        Assert.Equal((3, 3), DomainChecklist.Progress(DomainChecklist.For(domain)));
    }

    [Fact]
    public void ADomainNotCheckedYet_IsWaiting()
    {
        var domain = new Domain { Name = "new.example", FirstSeenUtc = DateTimeOffset.UtcNow };

        var items = DomainChecklist.For(domain).ToDictionary(item => item.Key);

        Assert.Equal(ChecklistState.Waiting, items["dmarc"].State);
        Assert.Equal(ChecklistState.Waiting, items["spf"].State);
        Assert.Equal(0, DomainChecklist.Progress(DomainChecklist.For(domain)).Done);
    }

    [Fact]
    public void TheReportPermissionRecord_IsTheProvidersJob_AndOnlyListedWhenItApplies()
    {
        var domain = AllGood();
        domain.DmarcAuthorizationCheckStatus = DmarcAuthorizationCheckStatus.Missing;

        var item = Assert.Single(DomainChecklist.For(domain), candidate => candidate.Key == "dmarc-auth");

        Assert.True(item.ByProvider);
        Assert.Equal(ChecklistState.ToDo, item.State);
        Assert.DoesNotContain(DomainChecklist.For(AllGood()), candidate => candidate.Key == "dmarc-auth");
    }
}

using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

/// <summary>The dashboard's Group filter has to name that Group's domains and no others. The Group list is alphabetical,
/// and coming back from another page restores the Group without loading the list again unless something makes it.</summary>
public class DashboardFilterTests
{
    private const int BcdrId = 4;
    private const int AcmeId = 5;
    private const int NorthId = 1;
    private const int PriorityTagId = 8;
    private const int OtherTagId = 3;

    private static readonly (int Id, string Name)[] Groups = [(AcmeId, "Acme"), (BcdrId, "BCDR"), (NorthId, "North")];
    private static readonly (int Id, string Name)[] Tags = [(PriorityTagId, "Priority"), (OtherTagId, "Later")];

    // 19 domains, the count James saw when BCDR (6 of them) stopped filtering. Two of the six also carry Priority,
    // and three domains outside BCDR carry it too, so a Tag is not the same cut as the Group.
    private static readonly DomainMembership[] Domains =
    [
        new("bcdr-1.test", [BcdrId], [PriorityTagId]),
        new("bcdr-2.test", [BcdrId], [PriorityTagId]),
        new("bcdr-3.test", [BcdrId], [OtherTagId]),
        new("bcdr-4.test", [BcdrId], []),
        new("bcdr-5.test", [BcdrId], []),
        new("bcdr-6.test", [BcdrId, AcmeId], []),
        new("acme-1.test", [AcmeId], [PriorityTagId]),
        new("acme-2.test", [AcmeId], [PriorityTagId]),
        new("acme-3.test", [AcmeId], [PriorityTagId]),
        new("north-1.test", [NorthId], []),
        new("north-2.test", [NorthId], [OtherTagId]),
        new("loose-1.test", [], []),
        new("loose-2.test", [], []),
        new("loose-3.test", [], [OtherTagId]),
        new("loose-4.test", [], []),
        new("loose-5.test", [], []),
        new("loose-6.test", [], []),
        new("loose-7.test", [], []),
        new("loose-8.test", [], []),
    ];

    [Fact]
    public void GroupFilter_ShowsExactlyThatGroupsDomains()
    {
        var shown = Shown(BcdrId, tagId: null);

        Assert.Equal(6, shown.Count);
        Assert.Equal(19, Domains.Length);
        Assert.All(shown, domain => Assert.Contains(BcdrId, domain.Groups));
        Assert.Equal(["bcdr-1.test", "bcdr-2.test", "bcdr-3.test", "bcdr-4.test", "bcdr-5.test", "bcdr-6.test"], shown.Select(domain => domain.Name));
    }

    [Fact]
    public void GroupFilter_AfterTheListIsSortedAlphabetically_StillUsesTheGroupId()
    {
        var sorted = DashboardSummary.OrderByName(Groups, group => group.Name);
        var bcdrIndex = sorted.FindIndex(group => group.Name == "BCDR");
        var bcdr = sorted[bcdrIndex];

        // Acme, BCDR, North. BCDR sits at index 1, but its id is 4. Using the index would select North.
        Assert.Equal(["Acme", "BCDR", "North"], sorted.Select(group => group.Name));
        Assert.Equal(1, bcdrIndex);
        Assert.Equal(BcdrId, bcdr.Id);
        Assert.NotEqual(bcdrIndex, bcdr.Id);

        var byId = Names(Shown(bcdr.Id, tagId: null));
        var byIndex = Names(Shown(bcdrIndex, tagId: null));

        Assert.Equal(["bcdr-1.test", "bcdr-2.test", "bcdr-3.test", "bcdr-4.test", "bcdr-5.test", "bcdr-6.test"], byId);
        Assert.Equal(["north-1.test", "north-2.test"], byIndex);
        Assert.NotEqual(byId, byIndex);
    }

    [Fact]
    public void GroupAndTag_ShowsOnlyDomainsInBoth()
    {
        var shown = Shown(BcdrId, PriorityTagId);

        Assert.Equal(["bcdr-1.test", "bcdr-2.test"], Names(shown));
        Assert.All(shown, domain =>
        {
            Assert.Contains(BcdrId, domain.Groups);
            Assert.Contains(PriorityTagId, domain.Tags);
        });
    }

    [Fact]
    public void SelectingAGroup_HidesTheUnfilteredListUntilItIsLoadedAgain()
    {
        var visit = new DashboardFilterSession();
        visit.Select(null, null);
        visit.MarkLoaded();
        Assert.True(visit.ListMatchesSelection);

        visit.Select(BcdrId, null);

        Assert.Equal(BcdrId, visit.GroupId);
        Assert.False(visit.ListMatchesSelection);
    }

    [Fact]
    public void NavigateAwayAndBack_ShowsTheRememberedGroup_NotEveryDomain()
    {
        var memory = new DashboardFilterMemory();
        var firstVisit = new DashboardFilterSession();
        firstVisit.Select(BcdrId, PriorityTagId);
        firstVisit.MarkLoaded();
        memory.Remember(firstVisit.GroupId, firstVisit.TagId);

        // The page instance is gone. The circuit (and the home page's redirect back to /dashboard, which has no
        // Group in the address) is not. A new visit has to load for the remembered filter before it shows a list.
        var returned = new DashboardFilterSession();
        var (groupId, tagId) = DashboardFilter.Resolve(
            queryGroupId: null, queryTagId: null, memory.GroupId, memory.TagId, Groups.Select(group => group.Id), Tags.Select(tag => tag.Id));
        returned.Select(groupId, tagId);

        Assert.Equal(BcdrId, returned.GroupId);
        Assert.Equal(PriorityTagId, returned.TagId);
        Assert.False(returned.ListMatchesSelection);
        Assert.Equal(["bcdr-1.test", "bcdr-2.test"], Names(Shown(returned.GroupId, returned.TagId)));
        Assert.NotEqual(Domains.Length, Shown(returned.GroupId, returned.TagId).Count);
    }

    [Fact]
    public void NavigateAwayAndBack_AfterTheGroupsAreReSorted_KeepsTheSameDomains()
    {
        var memory = new DashboardFilterMemory();
        memory.Remember(BcdrId, null);
        var sorted = DashboardSummary.OrderByName(Groups, group => group.Name);
        var (groupId, _) = DashboardFilter.Resolve(null, null, memory.GroupId, memory.TagId, sorted.Select(group => group.Id), Tags.Select(tag => tag.Id));

        Assert.Equal(BcdrId, groupId);
        Assert.Equal(["bcdr-1.test", "bcdr-2.test", "bcdr-3.test", "bcdr-4.test", "bcdr-5.test", "bcdr-6.test"], Names(Shown(groupId, null)));
    }

    [Fact]
    public void AnAddressWinsOverTheRememberedFilter()
    {
        var (groupId, tagId) = DashboardFilter.Resolve(AcmeId, null, rememberedGroupId: BcdrId, rememberedTagId: PriorityTagId, Groups.Select(group => group.Id), Tags.Select(tag => tag.Id));

        Assert.Equal(AcmeId, groupId);
        Assert.Null(tagId);
        Assert.Equal(["bcdr-6.test", "acme-1.test", "acme-2.test", "acme-3.test"], Names(Shown(groupId, tagId)));
    }

    [Fact]
    public void ARemovedGroup_IsNotSelected()
    {
        var (groupId, tagId) = DashboardFilter.Resolve(null, null, rememberedGroupId: 99, rememberedTagId: PriorityTagId, Groups.Select(group => group.Id), Tags.Select(tag => tag.Id));

        Assert.Null(groupId);
        Assert.Equal(PriorityTagId, tagId);
    }

    [Fact]
    public void Address_OmitsAClearedFilter()
    {
        Assert.Equal("/dashboard", DashboardFilter.Address(null, null));
        Assert.Equal("/dashboard?group=4", DashboardFilter.Address(BcdrId, null));
        Assert.Equal("/dashboard?group=4&tag=8", DashboardFilter.Address(BcdrId, PriorityTagId));
    }

    private static List<DomainMembership> Shown(int? groupId, int? tagId) =>
        DashboardFilter.Matching(Domains, groupId, tagId, domain => domain.Groups, domain => domain.Tags);

    private static List<string> Names(IEnumerable<DomainMembership> domains) => domains.Select(domain => domain.Name).ToList();

    private sealed record DomainMembership(string Name, int[] Groups, int[] Tags);
}

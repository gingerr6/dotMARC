using System.Net;
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class BlocklistCheckerTests
{
    private sealed class FakeResolver : IDnsblResolver
    {
        public Dictionary<string, string> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Queried { get; } = [];

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken cancellationToken)
        {
            Queried.Add(name);
            return Task.FromResult<IReadOnlyList<IPAddress>>(Answers.TryGetValue(name, out var answer) ? [IPAddress.Parse(answer)] : []);
        }
    }

    private readonly FakeTxtRecordLookup _txt = new();
    private readonly FakeMxHostsLookup _mx = new();
    private readonly FakeResolver _resolver = new();

    private BlocklistChecker Checker => new(_txt, _mx, _resolver);

    [Fact]
    public async Task A_listed_spf_address_is_reported()
    {
        _txt.TxtByName["contoso.com"] = ["v=spf1 ip4:203.0.113.5 ip4:198.51.100.0/24 include:spf.protection.outlook.com -all"];
        _resolver.Answers["5.113.0.203.zen.spamhaus.org"] = "127.0.0.2";

        var result = await Checker.CheckAsync("contoso.com", [], CancellationToken.None);

        Assert.Equal(BlocklistStatus.Listed, result.Status);
        Assert.Contains("203.0.113.5 (in SPF) on Spamhaus ZEN", result.Detail);
        Assert.DoesNotContain(_resolver.Queried, name => name.Contains("198.51.100"));
    }

    [Fact]
    public async Task A_refused_query_is_not_a_listing()
    {
        _txt.TxtByName["contoso.com"] = ["v=spf1 ip4:203.0.113.5 -all"];
        _resolver.Answers["5.113.0.203.zen.spamhaus.org"] = "127.255.255.254";

        var result = await Checker.CheckAsync("contoso.com", [], CancellationToken.None);

        Assert.Equal(BlocklistStatus.Clear, result.Status);
        Assert.Contains("Spamhaus ZEN refused", result.Detail);
    }

    [Fact]
    public async Task Own_mail_servers_and_approved_senders_are_checked_but_providers_are_not()
    {
        _mx.HostsByDomain["contoso.com"] = ["contoso-com.mail.protection.outlook.com", "mail.contoso.com"];
        _resolver.Answers["mail.contoso.com"] = "203.0.113.25";

        var result = await Checker.CheckAsync("contoso.com", ["198.51.100.7"], CancellationToken.None);

        Assert.Equal(BlocklistStatus.Clear, result.Status);
        Assert.DoesNotContain("contoso-com.mail.protection.outlook.com", _resolver.Queried);
        Assert.Contains("25.113.0.203.zen.spamhaus.org", _resolver.Queried);
        Assert.Contains("7.100.51.198.zen.spamhaus.org", _resolver.Queried);
        Assert.StartsWith("Checked 2 address(es)", result.Detail);
    }

    [Fact]
    public async Task Nothing_of_its_own_is_nothing_to_check()
    {
        _txt.TxtByName["contoso.com"] = ["v=spf1 include:_spf.google.com ip4:10.0.0.1 -all"];
        _mx.HostsByDomain["contoso.com"] = ["aspmx.l.google.com"];

        var result = await Checker.CheckAsync("contoso.com", [], CancellationToken.None);

        Assert.Equal(BlocklistStatus.NothingToCheck, result.Status);
    }
}

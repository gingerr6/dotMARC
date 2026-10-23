using System.Net;
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class DomainExpiryCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (DomainExpiryChecker Checker, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        return (new DomainExpiryChecker(new HttpClient(handler) { BaseAddress = new Uri("https://rdap.org/") }, new FixedTime()), handler);
    }

    private const string Nominet = """
        {"objectClassName":"domain","ldhName":"budgetwaste.co.uk",
         "events":[{"eventAction":"registration","eventDate":"2022-02-16T13:39:08Z"},{"eventAction":"expiration","eventDate":"2027-02-16T13:39:08Z"}],
         "entities":[{"roles":["registrar"],"vcardArray":["vcard",[["version",{},"text","4.0"],["fn",{},"text","20i Ltd"]]]},
                     {"roles":["registrant"],"vcardArray":["vcard",[["fn",{},"text",""]]]}]}
        """;

    [Fact]
    public async Task It_reads_the_expiry_date_and_registrar()
    {
        var (checker, handler) = Create();
        handler.ResponseBody = Nominet;

        var result = await checker.CheckAsync("budgetwaste.co.uk", CancellationToken.None);

        Assert.Equal((DomainExpiryStatus.Ok, new DateTimeOffset(2027, 2, 16, 13, 39, 8, TimeSpan.Zero), "20i Ltd"), (result.Status, result.ExpiresUtc, result.Registrar));
        Assert.Equal("https://rdap.org/domain/budgetwaste.co.uk", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task A_name_the_registry_doesnt_know_is_not_found()
    {
        var (checker, handler) = Create();
        handler.StatusCode = HttpStatusCode.NotFound;

        Assert.Equal(DomainExpiryStatus.NotFound, (await checker.CheckAsync("contoso.onmicrosoft.com", CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData(31, DomainExpiryStatus.Ok)]
    [InlineData(30, DomainExpiryStatus.ExpiringSoon)]
    [InlineData(3, DomainExpiryStatus.ExpiringSoon)]
    [InlineData(-1, DomainExpiryStatus.Expired)]
    public void Expiry_is_judged_against_thirty_days(int daysLeft, DomainExpiryStatus expected) =>
        Assert.Equal(expected, DomainExpiryChecker.Evaluate(Now.AddDays(daysLeft).AddHours(1), "20i Ltd", Now).Status);

    [Fact]
    public void No_expiry_date_published_is_not_found() =>
        Assert.Equal(DomainExpiryStatus.NotFound, DomainExpiryChecker.Evaluate(null, null, Now).Status);
}

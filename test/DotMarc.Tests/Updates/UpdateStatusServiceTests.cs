using System.Net;
using DotMarc.Tests.Internal;
using DotMarc.Updates;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DotMarc.Tests.Updates;

public sealed class UpdateStatusServiceTests
{
    private const string Closed = """
        [
          { "number": 12, "title": "Newest", "html_url": "https://github.com/o/r/pull/12", "user": { "login": "a" }, "merged_at": "2026-10-20T18:00:00Z", "updated_at": "2026-10-20T18:00:00Z", "merge_commit_sha": "ccc" },
          { "number": 11, "title": "Closed unmerged", "html_url": "u", "user": null, "merged_at": null, "updated_at": "2026-10-20T17:00:00Z", "merge_commit_sha": "zzz" },
          { "number": 10, "title": "Older", "html_url": "u", "user": null, "merged_at": "2026-10-20T16:00:00Z", "updated_at": "2026-10-20T16:00:00Z", "merge_commit_sha": "bbb" }
        ]
        """;

    private const string Open = """[ { "number": 13, "title": "In review", "html_url": "u", "user": null, "merged_at": null, "updated_at": "2026-10-20T18:30:00Z", "merge_commit_sha": null } ]""";

    private const string History = """[ { "sha": "bbb", "commit": { "committer": { "date": "2026-10-20T16:00:00Z" } } }, { "sha": "aaa", "commit": null } ]""";

    private static (UpdateStatusService Service, FakeHttpMessageHandler Handler) Create(string? commit, params string[] bodies)
    {
        var handler = new FakeHttpMessageHandler();
        foreach (var body in bodies)
        {
            handler.ResponseBodies.Enqueue(body);
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DOTMARC_REPOSITORY"] = "o/r",
            ["DOTMARC_COMMIT"] = commit,
        }).Build();
        return (new UpdateStatusService(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }, configuration, TimeProvider.System), handler);
    }

    [Fact]
    public async Task SortsMergedChanges_IntoInstalledAndWaiting_ByTheRunningCommitsHistory()
    {
        var (service, handler) = Create("bbb", Closed, Open, History);

        var status = await service.GetAsync();

        Assert.Null(status.Problem);
        Assert.Equal([10], status.Installed.Select(change => change.Number));
        Assert.Equal([12], status.Waiting.Select(change => change.Number));
        Assert.Equal([13], status.Open.Select(change => change.Number));
        Assert.Contains(handler.Requests, request => request.RequestUri!.PathAndQuery == "/repos/o/r/commits?sha=bbb&per_page=100");
    }

    [Fact]
    public async Task IsCached_UntilAskedToRefresh()
    {
        var (service, handler) = Create("bbb", Closed, Open, History, Closed, Open, History);

        await service.GetAsync();
        await service.GetAsync();
        Assert.Equal(3, handler.Requests.Count);

        await service.GetAsync(refresh: true);
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task WithoutTheRunningCommit_ListsMergedChangesAsUnknown()
    {
        var (service, _) = Create(null, Closed, Open);

        var status = await service.GetAsync();

        Assert.NotNull(status.Problem);
        Assert.Empty(status.Installed);
        Assert.Equal([12, 10], status.Waiting.Select(change => change.Number));
    }

    [Fact]
    public async Task SaysSo_WhenGitHubCantBeReached()
    {
        var (service, handler) = Create("bbb");
        handler.StatusCode = HttpStatusCode.Forbidden;

        var status = await service.GetAsync();

        Assert.StartsWith("Couldn't reach GitHub", status.Problem);
    }
}

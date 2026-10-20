using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Updates;

public sealed record ChangeSummary(int Number, string Title, string Url, string? Author, DateTimeOffset? MergedUtc, DateTimeOffset UpdatedUtc);

/// <summary>What's installed, what's merged but not installed yet, and what's still open, for the control panel.</summary>
public sealed record UpdateStatus(
    string? Repository,
    string? RunningCommit,
    IReadOnlyList<ChangeSummary> Installed,
    IReadOnlyList<ChangeSummary> Waiting,
    IReadOnlyList<ChangeSummary> Open,
    DateTimeOffset CheckedUtc,
    string? Problem = null);

/// <summary>Compares the pull requests merged into the repository dotMARC's image was built from with the commit it was
/// built from, both baked into the image (see the Dockerfile), using GitHub's public API. Cached for a few minutes, as
/// GitHub allows 60 unauthenticated calls an hour.</summary>
public sealed class UpdateStatusService(HttpClient http, IConfiguration configuration, TimeProvider time)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateStatus? _cached;

    public string? Repository => Clean(configuration["DOTMARC_REPOSITORY"]) ?? Clean(configuration["Updates:Repository"]);
    public string? RunningCommit => Clean(configuration["DOTMARC_COMMIT"]);

    public async Task<UpdateStatus> GetAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            if (!refresh && _cached is not null && now - _cached.CheckedUtc < CacheFor)
            {
                return _cached;
            }

            _cached = await LoadAsync(now, cancellationToken).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UpdateStatus> LoadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var repository = Repository;
        var commit = RunningCommit;
        if (repository is null)
        {
            return new UpdateStatus(null, commit, [], [], [], now, "This build of dotMARC doesn't say which repository it came from, so it can't list its updates.");
        }

        try
        {
            var closed = await http.GetFromJsonAsync<List<PullRequest>>($"repos/{repository}/pulls?state=closed&sort=updated&direction=desc&per_page=50", cancellationToken).ConfigureAwait(false) ?? [];
            var open = await http.GetFromJsonAsync<List<PullRequest>>($"repos/{repository}/pulls?state=open&sort=updated&direction=desc&per_page=20", cancellationToken).ConfigureAwait(false) ?? [];
            var merged = closed.Where(pull => pull.MergedAt is not null).OrderByDescending(pull => pull.MergedAt).ToList();

            if (commit is null)
            {
                return new UpdateStatus(repository, null, [], merged.Select(Summarise).ToList(), open.Select(Summarise).ToList(), now,
                    "This build doesn't say which commit it was built from, so it can't tell which of these are installed.");
            }

            // The running commit's history: a merged pull request is installed when its merge commit is in it. One with
            // a merge older than the oldest commit fetched is long since installed.
            var history = await http.GetFromJsonAsync<List<Commit>>($"repos/{repository}/commits?sha={commit}&per_page=100", cancellationToken).ConfigureAwait(false) ?? [];
            var shas = history.Select(item => item.Sha).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var oldest = history.Count == 100 ? history.Min(item => item.Details?.Committer?.Date) : null;
            bool IsInstalled(PullRequest pull) =>
                (pull.MergeCommitSha is { } sha && shas.Contains(sha)) || (oldest is { } cutoff && pull.MergedAt < cutoff);

            return new UpdateStatus(repository, commit,
                merged.Where(IsInstalled).Select(Summarise).ToList(),
                merged.Where(pull => !IsInstalled(pull)).Select(Summarise).ToList(),
                open.Select(Summarise).ToList(), now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new UpdateStatus(repository, commit, [], [], [], now, $"Couldn't reach GitHub to check for updates: {ex.Message}");
        }
    }

    private static ChangeSummary Summarise(PullRequest pull) =>
        new(pull.Number, pull.Title ?? $"#{pull.Number}", pull.HtmlUrl ?? "", pull.User?.Login, pull.MergedAt, pull.UpdatedAt);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PullRequest(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("user")] GitHubUser? User,
        [property: JsonPropertyName("merged_at")] DateTimeOffset? MergedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
        [property: JsonPropertyName("merge_commit_sha")] string? MergeCommitSha);

    private sealed record GitHubUser([property: JsonPropertyName("login")] string? Login);

    private sealed record Commit([property: JsonPropertyName("sha")] string Sha, [property: JsonPropertyName("commit")] CommitDetails? Details);

    private sealed record CommitDetails([property: JsonPropertyName("committer")] CommitPerson? Committer);

    private sealed record CommitPerson([property: JsonPropertyName("date")] DateTimeOffset? Date);
}

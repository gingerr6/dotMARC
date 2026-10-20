using System.Diagnostics;
using Npgsql;

namespace DotMarc.Backups;

/// <summary>Dumps and restores dotMARC's database: pg_dump and pg_restore (PgDatabaseDumper), or a fake in tests.</summary>
public interface IDatabaseDumper
{
    /// <summary>Null when it can be used; otherwise why not, for the Backups page.</summary>
    string? Unavailable { get; }

    Task DumpAsync(string filePath, CancellationToken cancellationToken);
    Task RestoreAsync(string filePath, CancellationToken cancellationToken);
}

/// <summary>Runs pg_dump, pg_restore and psql (in dotMARC's image, see the Dockerfile) against the configured database.</summary>
public sealed class PgDatabaseDumper(string connectionString) : IDatabaseDumper
{
    private static readonly Lazy<string?> MissingTools = new(() =>
        new[] { "pg_dump", "pg_restore", "psql" }.Any(tool => Find(tool) is null)
            ? "pg_dump isn't installed where dotMARC runs, so it can't back up its database."
            : null);

    public string? Unavailable => MissingTools.Value;

    public Task DumpAsync(string filePath, CancellationToken cancellationToken) =>
        RunAsync("pg_dump", connect: true, ["--format=custom", "--file", filePath], cancellationToken);

    /// <summary>Empties the database and loads the dump into it, in one transaction: if anything fails, nothing
    /// changes. Emptying it first, rather than only replacing what the dump holds, matters when the backup is older
    /// than this version of dotMARC: tables added since would otherwise survive, and the migrations that add them would
    /// fail when dotMARC next starts.</summary>
    public async Task RestoreAsync(string filePath, CancellationToken cancellationToken)
    {
        var script = filePath + ".sql";
        await RunAsync("pg_restore", connect: false, ["--no-owner", "--no-privileges", "--file", script, filePath], cancellationToken).ConfigureAwait(false);
        await RunAsync("psql", connect: true,
            ["--quiet", "--set", "ON_ERROR_STOP=1", "--single-transaction", "--command", "DROP SCHEMA public CASCADE", "--command", "CREATE SCHEMA public", "--file", script],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(string tool, bool connect, string[] arguments, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var start = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true };
        var connection = connect
            ? new[] { "--host", builder.Host ?? "localhost", "--port", builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--username", builder.Username ?? "postgres", "--dbname", builder.Database ?? "postgres", "--no-password" }
            : [];
        foreach (var argument in connection.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        // In the environment, never on the command line, where other processes could read it.
        start.Environment["PGPASSWORD"] = builder.Password ?? "";
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Couldn't start {tool}.");
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var message = (await errors.ConfigureAwait(false)).Trim();
            throw new InvalidOperationException($"{tool} failed: {(message.Length > 500 ? message[..500] : message)}");
        }
    }

    private static string? Find(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, tool))
            .FirstOrDefault(File.Exists);
}

/// <summary>Stands in where backups must not run, such as the public demo.</summary>
public sealed class DisabledDatabaseDumper(string reason) : IDatabaseDumper
{
    public string? Unavailable => reason;
    public Task DumpAsync(string filePath, CancellationToken cancellationToken) => throw new InvalidOperationException(reason);
    public Task RestoreAsync(string filePath, CancellationToken cancellationToken) => throw new InvalidOperationException(reason);
}

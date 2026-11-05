using System.Diagnostics;
using Xunit;

namespace DotMarc.Tests.Components;

/// <summary>The resume script runs in the browser, so the cases live in reconnect.test.js and this runs them with
/// node. The assertions here only check that the page actually loads that script.</summary>
public sealed class ReconnectResumeTests
{
    [Fact]
    public void ThePage_LoadsTheResumeScript()
    {
        var razor = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DotMarc", "Components", "App.razor"));

        Assert.Contains("src=\"@Assets[\"reconnect.js\"]\"", razor, StringComparison.Ordinal);
        Assert.Contains("id=\"components-reconnect-modal\"", razor, StringComparison.Ordinal);
        // The behaviour lives in one script. A second observer here would reload on top of it.
        Assert.DoesNotContain("MutationObserver", razor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumeScript_MatchesTheCases()
    {
        var root = RepoRoot();
        var start = new ProcessStartInfo
        {
            FileName = NodePath(),
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--test");
        start.ArgumentList.Add(Path.Combine(root, "test", "DotMarc.Tests", "Components", "reconnect.test.js"));

        using var process = Process.Start(start);
        Assert.NotNull(process);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdoutTask + Environment.NewLine + await stderrTask;

        Assert.True(process.ExitCode == 0, output);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "dotMARC.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find dotMARC.sln above the test output.");
    }

    private static string NodePath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "node.exe" : "node");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return "node";
    }
}

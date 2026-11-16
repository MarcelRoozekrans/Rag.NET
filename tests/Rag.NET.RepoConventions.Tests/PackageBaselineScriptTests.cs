using System.Diagnostics;
using System.Net;
using System.Text;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Executes <c>.github/scripts/package-baselines.sh</c> against a loopback server standing in for
/// the NuGet flat container, and asserts which baseline it picks for each package.
/// </summary>
/// <remarks>
/// <b>The script is run, not read.</b> The baseline decides what the API compatibility check
/// compares against, so a script that picked the wrong version, or treated a failed lookup as
/// "nothing to compare", would turn the check into a pass that proves nothing.
/// </remarks>
public sealed class PackageBaselineScriptTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Prereleases never count, and the highest stable version below the packing version wins.</summary>
    [Fact]
    public async Task PicksTheHighestStableVersionBelowThePackingVersion()
    {
        var result = await RunAsync("1.9.3-pr.4", ["Rag.NET.Abstractions"],
            ("rag.net.abstractions", 200, Versions("1.9.0", "1.9.2", "1.10.0-preview.1")));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1.9.2", result.BaselineOf("Rag.NET.Abstractions"));
    }

    /// <summary>A re-pack of a published version is compared with its predecessor, not with itself.</summary>
    [Fact]
    public async Task APublishedVersionEqualToThePackingVersionIsNotItsOwnBaseline()
    {
        var result = await RunAsync("1.9.2", ["Rag.NET.Abstractions"],
            ("rag.net.abstractions", 200, Versions("1.9.1", "1.9.2")));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1.9.1", result.BaselineOf("Rag.NET.Abstractions"));
    }

    /// <summary>A major release is validated against the last release of the previous major.</summary>
    [Fact]
    public async Task AStableReleaseIsValidatedAgainstItsPredecessorAcrossAMajor()
    {
        var result = await RunAsync("2.0.0", ["Rag.NET.Abstractions"],
            ("rag.net.abstractions", 200, Versions("1.9.2")));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1.9.2", result.BaselineOf("Rag.NET.Abstractions"));
    }

    /// <summary>Versions compare per numeric field: 1.10.0 is above 1.9.2, though it sorts below it as text.</summary>
    [Fact]
    public async Task VersionsAreComparedNumericallyNotLexically()
    {
        var result = await RunAsync("2.0.0", ["Rag.NET.Abstractions"],
            ("rag.net.abstractions", 200, Versions("1.9.2", "1.10.0")));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1.10.0", result.BaselineOf("Rag.NET.Abstractions"));
    }

    /// <summary>An id nuget.org has never seen is a new package: it is listed, and the lookup succeeds.</summary>
    [Fact]
    public async Task ANotFoundIdIsListedAsUnpublishedAndSucceeds()
    {
        var result = await RunAsync("1.9.3", ["Rag.NET.New"],
            ("rag.net.new", 404, "{}"));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("RagNetUnpublishedPackage Include=\"Rag.NET.New\"", result.Props, StringComparison.Ordinal);
        Assert.Null(result.BaselineOf("Rag.NET.New"));
        Assert.Contains("Rag.NET.New", result.Output, StringComparison.Ordinal);
    }

    /// <summary>An id with no stable version has nothing to be compatible with.</summary>
    [Fact]
    public async Task OnlyPrereleasesCountsAsUnpublished()
    {
        var result = await RunAsync("0.1.0-pr.2", ["Rag.NET.Early"],
            ("rag.net.early", 200, Versions("0.1.0-preview.3")));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("RagNetUnpublishedPackage Include=\"Rag.NET.Early\"", result.Props, StringComparison.Ordinal);
        Assert.Null(result.BaselineOf("Rag.NET.Early"));
    }

    /// <summary>
    /// An id that has stable versions, none of them below the packing version, is an error: it is
    /// not the same as a package that was never published.
    /// </summary>
    [Fact]
    public async Task AnIdWithOnlyHigherStableVersionsFails()
    {
        var result = await RunAsync("1.9.3", ["Rag.NET.Ahead"],
            ("rag.net.ahead", 200, Versions("2.0.0")));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Rag.NET.Ahead", result.Output, StringComparison.Ordinal);
    }

    /// <summary>A server error is not "no baseline"; swallowing it would skip the check silently.</summary>
    [Fact]
    public async Task AServerErrorFailsTheLookup()
    {
        var result = await RunAsync("1.9.3", ["Rag.NET.Abstractions"],
            ("rag.net.abstractions", 500, "boom"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("::error::", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A suppression file for a major below the baseline's major can no longer match anything and
    /// is reported for deletion. The file for the baseline's own major is kept.
    /// </summary>
    [Fact]
    public async Task AnOlderMajorSuppressionFileIsReportedForDeletion()
    {
        var sourceRoot = Directory.CreateTempSubdirectory("ragnet-baseline-src-").FullName;
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(sourceRoot, "Foo")).FullName;
            var stale = Path.Combine(project, "CompatibilitySuppressions.1.x.xml");
            var current = Path.Combine(project, "CompatibilitySuppressions.2.x.xml");
            File.WriteAllText(stale, "<Suppressions />");
            File.WriteAllText(current, "<Suppressions />");

            var result = await RunAsync("2.1.0", ["Foo"], sourceRoot, string.Empty,
                ("foo", 200, Versions("1.9.2", "2.0.0")));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("2.0.0", result.BaselineOf("Foo"));
            var warning = result.Output
                .Split('\n')
                .Single(line => line.Contains(stale, StringComparison.Ordinal));
            Assert.StartsWith("::warning::", warning, StringComparison.Ordinal);
            Assert.DoesNotContain(current, result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
        }
    }

    /// <summary>
    /// The header comment quotes the source URL, and "--" must not appear in an XML comment. A URL
    /// with a run of hyphens must still produce a props file that parses.
    /// </summary>
    [Fact]
    public async Task AHyphenatedSourceUrlStillProducesValidXml()
    {
        var result = await RunAsync(
            "1.9.3",
            ["Rag.NET.Abstractions"],
            Path.Combine(Path.GetTempPath(), "ragnet-no-such-source-root"),
            "a---b----",
            ("a---b----/rag.net.abstractions", 200, Versions("1.9.2")));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1.9.2", result.BaselineOf("Rag.NET.Abstractions"));
        var document = System.Xml.Linq.XDocument.Parse(result.Props);
        Assert.NotNull(document.Root);
    }

    private static string Versions(params string[] versions) =>
        "{\"versions\":[" + string.Join(",", versions.Select(v => $"\"{v}\"")) + "]}";

    private static Task<ScriptResult> RunAsync(
        string packingVersion,
        string[] ids,
        params (string Id, int Status, string Body)[] routes) =>
        RunAsync(packingVersion, ids, Path.Combine(Path.GetTempPath(), "ragnet-no-such-source-root"), string.Empty, routes);

    private static async Task<ScriptResult> RunAsync(
        string packingVersion,
        string[] ids,
        string sourceRoot,
        string containerPath,
        params (string Id, int Status, string Body)[] routes)
    {
        var root = TestProject.FindRepositoryRoot();
        var script = Path.Combine(root, ".github", "scripts", "package-baselines.sh");
        Assert.True(File.Exists(script), $"'{script}' does not exist.");

        var props = Path.Combine(Path.GetTempPath(), $"ragnet-baselines-{Guid.NewGuid():N}.props");
        var (listener, prefix) = StartListener();
        var serving = ServeAsync(listener, routes);

        try
        {
            var (exitCode, output) = await RunScriptAsync(script, root, packingVersion, props, sourceRoot, prefix + containerPath, ids);
            var written = File.Exists(props) ? File.ReadAllText(props) : string.Empty;
            return new ScriptResult(exitCode, output, written);
        }
        finally
        {
            // Close alone, not Stop and then Dispose: on Linux a Stop followed by Dispose re-resolves
            // the endpoint by port to remove the prefix, and throws if another test host has taken it.
            listener.Close();
            try
            {
                await serving;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // Stopped.
            }

            File.Delete(props);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunScriptAsync(
        string script, string root, string packingVersion, string props, string sourceRoot, string prefix, string[] ids)
    {
        var startInfo = new ProcessStartInfo(FindShell())
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add(packingVersion);
        startInfo.ArgumentList.Add(props);
        startInfo.ArgumentList.Add(sourceRoot);
        startInfo.Environment["NUGET_FLAT_CONTAINER"] = prefix.TrimEnd('/');

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned null for bash.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(string.Join("\n", ids) + "\n");
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(RunTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"package-baselines.sh did not exit within {RunTimeout.TotalSeconds:0}s.");
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    /// <summary>
    /// Binds a listener on a random loopback port. <see cref="HttpListener"/> cannot bind port 0, so
    /// this tries ports until <c>Start</c> succeeds. The listener that is returned is the one that
    /// holds the port: probing with a throwaway listener and releasing it first would leave a gap in
    /// which the other target framework's test host, running in parallel, could take the port.
    /// </summary>
    private static (HttpListener Listener, string Prefix) StartListener()
    {
        var random = new Random();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var prefix = $"http://127.0.0.1:{random.Next(20000, 60000)}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                return (listener, prefix);
            }
            catch (HttpListenerException)
            {
                listener.Abort();
            }
        }

        throw new InvalidOperationException("No free loopback port found.");
    }

    private static async Task ServeAsync(HttpListener listener, (string Id, int Status, string Body)[] routes)
    {
        var table = new Dictionary<string, (int Status, string Body)>(StringComparer.Ordinal);
        foreach (var (id, status, body) in routes)
        {
            table[$"/{id}/index.json"] = (status, body);
        }

        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            var path = context.Request.Url!.AbsolutePath;
            var (status, body) = table.TryGetValue(path, out var route) ? route : (404, "{}");

            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private sealed record ScriptResult(int ExitCode, string Output, string Props)
    {
        public string? BaselineOf(string id)
        {
            var marker = $"'$(PackageId)' == '{id}'";
            var start = Props.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            const string open = "<PackageValidationBaselineVersion>";
            var from = Props.IndexOf(open, start, StringComparison.Ordinal) + open.Length;
            var to = Props.IndexOf("</PackageValidationBaselineVersion>", from, StringComparison.Ordinal);
            return Props[from..to];
        }
    }

    private static string FindShell()
    {
        string[] candidates =
        [
            "/bin/bash",
            "/usr/bin/bash",
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files\Git\usr\bin\bash.exe",
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "No bash found to execute .github/scripts/package-baselines.sh. Looked in: " +
            string.Join(", ", candidates) + ".");
    }
}

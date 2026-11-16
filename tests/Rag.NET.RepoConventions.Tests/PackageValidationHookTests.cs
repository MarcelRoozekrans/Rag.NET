using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Covers the package-validation hook in the root <c>Directory.Build.targets</c> (#846): given the
/// generated baselines file, it turns on the SDK's package validation for the packages that have a
/// baseline and points the suppression file at the one for the baseline's major.
/// </summary>
/// <remarks>
/// Each test evaluates a throwaway project with <c>dotnet msbuild</c> and reads the evaluated
/// properties and items as JSON. The project lives under this test's output directory, which sits
/// below the repository root, so the root <c>Directory.Build.targets</c> is the one imported. Every
/// test asserts that, so none of them can pass because the hook was never in play.
/// </remarks>
public sealed class PackageValidationHookTests : IDisposable
{
    private const string PackageId = "Rag.NET.HookProbe";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory, "package-validation-hook", Guid.NewGuid().ToString("N"));

    public PackageValidationHookTests()
    {
        Directory.CreateDirectory(_directory);
        WriteProject($"<PackageId>{PackageId}</PackageId>");
    }

    private void WriteProject(string packageIdElement) =>
        File.WriteAllText(
            Path.Combine(_directory, "Probe.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                {packageIdElement}
              </PropertyGroup>
            </Project>
            """);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void WithABaselineValidationIsOnAndTheSuppressionFileIsPerMajor()
    {
        var result = Evaluate(BaselinesFor(PackageId, "1.9.2"));

        Assert.Equal("true", result.Property("EnablePackageValidation"));
        Assert.EndsWith("CompatibilitySuppressions.1.x.xml", result.Property("ApiCompatSuppressionOutputFile"), StringComparison.Ordinal);
        Assert.Equal(_directory, Path.GetDirectoryName(result.Property("ApiCompatSuppressionOutputFile")));
    }

    [Fact]
    public void TheSuppressionItemIsAddedOnlyWhenTheFileExists()
    {
        var baselines = BaselinesFor(PackageId, "1.9.2");

        Assert.Empty(Evaluate(baselines).SuppressionItems);

        File.WriteAllText(Path.Combine(_directory, "CompatibilitySuppressions.1.x.xml"), "<Suppressions />");

        var item = Assert.Single(Evaluate(baselines).SuppressionItems);
        Assert.EndsWith("CompatibilitySuppressions.1.x.xml", item, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderMajorFileIsNotPassedToTheSdk()
    {
        File.WriteAllText(Path.Combine(_directory, "CompatibilitySuppressions.1.x.xml"), "<Suppressions />");

        var result = Evaluate(BaselinesFor(PackageId, "2.0.0"));

        Assert.Empty(result.SuppressionItems);
        Assert.EndsWith("CompatibilitySuppressions.2.x.xml", result.Property("ApiCompatSuppressionOutputFile"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultPackageIdFromTheProjectNameMatchesTheBaselinesCondition()
    {
        WriteProject(string.Empty);

        var result = Evaluate(BaselinesFor("Probe", "1.9.2"));

        Assert.Equal("true", result.Property("EnablePackageValidation"));
    }

    [Fact]
    public void WithoutThePropertyNothingChanges()
    {
        var result = Evaluate(baselinesFile: null);

        Assert.Equal(string.Empty, result.Property("EnablePackageValidation"));
        Assert.Equal(string.Empty, result.Property("ApiCompatSuppressionOutputFile"));
    }

    [Fact]
    public void APackageWithoutABaselineIsNotValidated()
    {
        var result = Evaluate(BaselinesFor("Rag.NET.SomethingElse", "1.9.2"));

        Assert.Equal(string.Empty, result.Property("EnablePackageValidation"));
        Assert.Empty(result.SuppressionItems);
    }

    private string BaselinesFor(string packageId, string version)
    {
        var path = Path.Combine(_directory, "package-baselines.props");
        File.WriteAllText(
            path,
            $"""
            <Project>
              <PropertyGroup Condition="'$(PackageId)' == '{packageId}'">
                <PackageValidationBaselineVersion>{version}</PackageValidationBaselineVersion>
              </PropertyGroup>
            </Project>
            """);
        return path;
    }

    private EvaluationResult Evaluate(string? baselinesFile)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(Path.Combine(_directory, "Probe.csproj"));
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-nodeReuse:false");
        startInfo.ArgumentList.Add("-getProperty:EnablePackageValidation");
        startInfo.ArgumentList.Add("-getProperty:ApiCompatSuppressionOutputFile");
        startInfo.ArgumentList.Add("-getProperty:DirectoryBuildTargetsPath");
        startInfo.ArgumentList.Add("-getItem:ApiCompatSuppressionFile");

        // An inherited variable becomes an MSBuild property and would defeat the no-property test.
        startInfo.Environment.Remove("RagNetPackageBaselines");
        if (baselinesFile is not null)
        {
            startInfo.ArgumentList.Add($"-p:RagNetPackageBaselines={baselinesFile}");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned null for dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        Assert.True(
            process.WaitForExit((int)RunTimeout.TotalMilliseconds),
            $"`dotnet msbuild` did not exit within {RunTimeout.TotalSeconds:0}s.");

        Assert.True(process.ExitCode == 0, stdout.Result + stderr.Result);

        using var json = JsonDocument.Parse(stdout.Result);
        var root = json.RootElement;
        var properties = root.GetProperty("Properties");
        var items = root.GetProperty("Items");

        var suppressionItems = items.TryGetProperty("ApiCompatSuppressionFile", out var list)
            ? list.EnumerateArray().Select(i => i.GetProperty("FullPath").GetString()!).ToArray()
            : [];

        var result = new EvaluationResult(
            properties.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal),
            suppressionItems);

        // Without this the tests could pass while the hook was never in play.
        Assert.Equal(
            Path.Combine(TestProject.FindRepositoryRoot(), "Directory.Build.targets"),
            result.Property("DirectoryBuildTargetsPath"));

        return result;
    }

    private sealed record EvaluationResult(IReadOnlyDictionary<string, string> Properties, string[] SuppressionItems)
    {
        public string Property(string name) => Properties[name];
    }
}

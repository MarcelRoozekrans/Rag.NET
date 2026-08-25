using System.Xml.Linq;
using Xunit;

namespace Rag.NET.RepoConventions.Tests;

/// <summary>
/// Asserts that <c>Rag.NET.Security</c> does not depend on SQLite, and that the audit log which
/// does lives in its own package.
/// </summary>
/// <remarks>
/// <para>
/// Phase 6.2.6 (#339). A single file — <c>Audit/SqliteAuditLog.cs</c> — was the only thing in
/// <c>Rag.NET.Security</c> that touched SQLite, and it put <c>Microsoft.Data.Sqlite</c> plus a
/// native <c>SQLitePCLRaw</c> binary on every consumer of <c>UseChunkSanitiser</c>,
/// <c>UseRbac</c> and <c>UsePiiDetection</c> — none of which load it.
/// </para>
/// <para>
/// This is a guard rather than a one-off cleanup because the regression is invisible: adding a
/// <c>PackageReference</c> back would compile, test green, and ship a native binary to everyone
/// again. Nothing else in the suite would notice. The same reasoning produced
/// <c>Rag.NET.Storage.Sqlite</c> at 0.1.0.
/// </para>
/// </remarks>
public sealed class SecurityPackageWeightTests
{
    private const string SecurityProject = "src/Rag.NET.Security/Rag.NET.Security.csproj";
    private const string AuditSqliteProject =
        "src/Rag.NET.Security.Audit.Sqlite/Rag.NET.Security.Audit.Sqlite.csproj";

    /// <summary>
    /// <c>Rag.NET.Security</c> declares four package references today. A parse yielding none would
    /// make the SQLite assertion below pass over an empty set — vacuously, and forever.
    /// </summary>
    private const int FewestPlausiblePackageReferences = 2;

    [Fact]
    public void RagNetSecurity_DoesNotDependOnSqlite()
    {
        var references = PackageReferenceIdsOf(SecurityProject);

        Assert.True(
            references.Count >= FewestPlausiblePackageReferences,
            $"Found only {references.Count} PackageReference entries in {SecurityProject}. The " +
            "project moved or the parse failed, and the SQLite assertion would pass over nothing.");

        var sqlite = references.FindAll(static id =>
            id.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
            || id.Contains("SQLitePCL", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            sqlite.Count == 0,
            $"{SecurityProject} depends on {string.Join(", ", sqlite)}. Audit logging that needs " +
            "SQLite belongs in Rag.NET.Security.Audit.Sqlite (#339) — otherwise every consumer of " +
            "UseChunkSanitiser, UseRbac and UsePiiDetection ships a native binary they never load.");
    }

    /// <summary>
    /// The other half: the dependency has to have gone <i>somewhere</i>. Without this, deleting
    /// the audit log outright would satisfy the test above.
    /// </summary>
    [Fact]
    public void RagNetSecurityAuditSqlite_IsWhereTheSqliteDependencyLives()
    {
        var references = PackageReferenceIdsOf(AuditSqliteProject);

        Assert.Contains(
            references,
            static id => id.Equals("Microsoft.Data.Sqlite", StringComparison.Ordinal));
    }

    /// <summary>
    /// Reads a project's <c>PackageReference</c> ids, failing loudly if the file is missing rather
    /// than returning an empty list that every caller would then assert happily over.
    /// </summary>
    /// <param name="relativePath">Repository-relative path to the project file.</param>
    /// <returns>Every declared <c>PackageReference</c> id.</returns>
    private static List<string> PackageReferenceIdsOf(string relativePath)
    {
        var path = Path.Combine(TestProject.FindRepositoryRoot(), relativePath);
        Assert.True(File.Exists(path), $"Expected a project file at {path}.");

        return [.. XDocument.Load(path)
            .Descendants("PackageReference")
            .Select(static reference => reference.Attribute("Include")?.Value ?? string.Empty)
            .Where(static id => id.Length > 0)];
    }
}

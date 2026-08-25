using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.NET.Abstractions;
using Rag.NET.DependencyInjection;

namespace Rag.NET.Security;

/// <summary>Registers SQLite-backed audit logging on a <see cref="IRagBuilder"/>.</summary>
public static class SqliteAuditLogBuilderExtensions
{
    /// <summary>
    /// Records retrieval and answer events to a SQLite database file, wiring the audit behaviour
    /// and the answer-engine decorator alongside the log itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This replaces <c>UseAuditLog()</c>, which no longer exists.</b> That method lived in
    /// <c>Rag.NET.Security</c> and registered <see cref="SqliteAuditLog"/>, which is why every
    /// consumer of <c>UseChunkSanitiser</c>, <c>UseRbac</c> or <c>UsePiiDetection</c> shipped a
    /// native SQLite binary they never loaded (#339).
    /// </para>
    /// <para>
    /// <b>Migrating from 0.1.0</b> is two steps and no <c>using</c> changes — the namespace is
    /// deliberately unchanged:
    /// </para>
    /// <list type="number">
    /// <item><description>Add a package reference to <c>Rag.NET.Security.Audit.Sqlite</c>.</description></item>
    /// <item><description>Rename <c>UseAuditLog(…)</c> to <c>UseSqliteAuditLog(…)</c>.</description></item>
    /// </list>
    /// <para>
    /// Forgetting step 2 is a <b>compile error</b>, not a silent gap. The wiring that registers the
    /// behaviour and the decorator is internal to <c>Rag.NET.Security</c> and reachable only from a
    /// package that also supplies an <see cref="IAuditLog"/>, so "auditing configured, nothing
    /// recorded" cannot be expressed. An audit log that silently records nothing is worse than a
    /// build error.
    /// </para>
    /// </remarks>
    /// <typeparam name="TBuilder">The builder being configured.</typeparam>
    /// <param name="builder">The builder being configured.</param>
    /// <param name="configure">
    /// Optional callback over <see cref="AuditLogOptions"/> — notably
    /// <see cref="AuditLogOptions.DatabasePath"/>, and the <c>LogQueryText</c> /
    /// <c>LogAnswerText</c> switches that decide whether raw text is persisted at all.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder UseSqliteAuditLog<TBuilder>(
        this TBuilder builder, Action<AuditLogOptions>? configure = null)
        where TBuilder : IRagBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        var opts = new AuditLogOptions();
        configure?.Invoke(opts);

        // Registered before the wiring below, which resolves IAuditLog out of the container.
        builder.Services.AddSingleton<IAuditLog>(sp =>
            new SqliteAuditLog(opts, sp.GetService<ILogger<SqliteAuditLog>>()));

        return builder.AddAuditWiring(opts);
    }
}

using Microsoft.EntityFrameworkCore;

namespace OrionAudit.Testing;

/// <summary>
/// Snapshot of audit-log rows from a <see cref="DbContext"/> for use in fluent test assertions.
/// </summary>
public sealed class AuditCapture
{
    private readonly List<AuditLog> rows;

    private AuditCapture(List<AuditLog> rows) => this.rows = rows;

    /// <summary>All captured audit rows.</summary>
    public IReadOnlyList<AuditLog> All => rows;

    /// <summary>Loads all audit rows from the supplied context (sync — for in-memory test stores).</summary>
    public static AuditCapture From(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var loaded = context.Set<AuditLog>().AsNoTracking().ToList();
        return new AuditCapture(loaded);
    }

    /// <summary>Returns audit rows for entity type <typeparamref name="T"/>.</summary>
    public IEnumerable<AuditLog> For<T>()
        => rows.Where(r => r.EntityType == typeof(T).AssemblyQualifiedName);

    /// <summary>Entry point for fluent assertions.</summary>
    public AuditAssertions Should() => new(this);
}

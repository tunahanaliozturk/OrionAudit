namespace Moongazing.OrionAudit;

/// <summary>The kind of mutation captured by an <see cref="AuditLog"/> row.</summary>
public enum AuditAction : byte
{
    /// <summary>Entity was inserted into the database.</summary>
    Inserted = 0,
    /// <summary>Entity was updated in the database.</summary>
    Updated = 1,
    /// <summary>Entity was deleted from the database.</summary>
    Deleted = 2,
    /// <summary>
    /// Entity was logically deleted by flipping a soft-delete property from <c>false</c> to
    /// <c>true</c>. Distinct from <see cref="Deleted"/> so reads can surface live deletions vs.
    /// hard removals.
    /// </summary>
    SoftDeleted = 3,
}

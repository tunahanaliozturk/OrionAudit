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
}

namespace Moongazing.OrionAudit.Configuration;

/// <summary>Frozen, thread-safe runtime configuration of audited types. Built once at startup.</summary>
public interface IAuditConfiguration
{
    /// <summary>True if the type was registered for audit (via attribute or fluent builder).</summary>
    bool IsAudited(Type entityType);

    /// <summary>The per-type configuration, or null if the type is not audited.</summary>
    AuditableTypeConfig? GetConfig(Type entityType);
}

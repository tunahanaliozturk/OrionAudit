namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Defines what set of <see cref="AuditLog"/> rows forms a single tamper-evident hash chain, i.e.
/// which row counts as the "immediately preceding entry" when computing
/// <see cref="AuditLog.PreviousHash"/>.
/// </summary>
public enum AuditHashChainScope
{
    /// <summary>
    /// One independent chain per entity stream: the rows sharing an
    /// (<see cref="AuditLog.EntityType"/>, <see cref="AuditLog.EntityId"/>) key, ordered by
    /// (<see cref="AuditLog.OccurredOnUtc"/>, <see cref="AuditLog.Id"/>). This is the default and
    /// aligns with how the store already indexes, compacts, and reconstructs history (per entity),
    /// so each entity's trail is independently verifiable and a gap in one stream does not implicate
    /// another. The tenant id is part of each row's hashed content, so cross-tenant tampering on a
    /// shared entity id is still detected even though the chain key itself omits the tenant.
    /// </summary>
    PerEntityStream = 0,
}

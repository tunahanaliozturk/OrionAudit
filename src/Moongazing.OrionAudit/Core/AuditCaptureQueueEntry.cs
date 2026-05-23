namespace Moongazing.OrionAudit;

/// <summary>
/// A pending audit capture awaiting background dispatch. Written by
/// <c>AuditSaveChangesInterceptor</c> in async mode, in the same transaction as the
/// originating entity change, then consumed by <c>AuditDispatcherHostedService</c> which
/// computes the diff, writes the final <see cref="AuditLog"/> row, and deletes this row.
/// </summary>
public sealed class AuditCaptureQueueEntry
{
    /// <summary>Auto-increment surrogate key; also the dispatch order key.</summary>
    public long Id { get; set; }

    /// <summary>Assembly-qualified name of the audited entity type.</summary>
    public string EntityType { get; set; } = default!;

    /// <summary>Serialized primary key of the audited entity (canonical <see cref="AuditKey"/> form).</summary>
    public string EntityId { get; set; } = default!;

    /// <summary>What kind of change this row records.</summary>
    public AuditAction Action { get; set; }

    /// <summary>Rule-applied before-state snapshot JSON (hash/redact/exclude already applied).</summary>
    public string BeforeJson { get; set; } = default!;

    /// <summary>Rule-applied after-state snapshot JSON (hash/redact/exclude already applied).</summary>
    public string AfterJson { get; set; } = default!;

    /// <summary>Optional user id captured at write time.</summary>
    public string? UserId { get; set; }

    /// <summary>Optional human-readable user display name.</summary>
    public string? UserDisplay { get; set; }

    /// <summary>Optional user classification.</summary>
    public string? UserType { get; set; }

    /// <summary>Optional tenant id captured at write time.</summary>
    public string? TenantId { get; set; }

    /// <summary>Optional correlation id captured at write time.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>UTC timestamp of the originating change; copied verbatim onto the final <see cref="AuditLog"/>.</summary>
    public DateTime OccurredOnUtc { get; set; }

    /// <summary>Dispatch attempts so far; drives dead-lettering.</summary>
    public int Attempts { get; set; }

    /// <summary>Null until dead-lettered, then the failure detail. A non-null value excludes the row from dispatch.</summary>
    public string? Error { get; set; }

    /// <summary>Per-dispatcher claim token; null when unclaimed.</summary>
    public string? ClaimToken { get; set; }

    /// <summary>UTC time the current claim was taken; used with the claim lease to reclaim abandoned rows.</summary>
    public DateTime? ClaimedUtc { get; set; }
}

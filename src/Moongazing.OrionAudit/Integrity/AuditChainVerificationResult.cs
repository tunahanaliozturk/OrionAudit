namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Why a tamper-evident chain failed verification. Reported alongside the offending row so an
/// operator knows what kind of integrity violation was detected.
/// </summary>
public enum AuditChainBreakReason
{
    /// <summary>No break; the chain verified intact.</summary>
    None = 0,

    /// <summary>
    /// The row's stored <see cref="AuditLog.EntryHash"/> does not match the hash recomputed from its
    /// content and <see cref="AuditLog.PreviousHash"/>. The row's content was altered after it was
    /// written (or its stored hash was tampered with).
    /// </summary>
    ContentMismatch = 1,

    /// <summary>
    /// The row's <see cref="AuditLog.PreviousHash"/> does not match the <see cref="AuditLog.EntryHash"/>
    /// of the row that actually precedes it in chain order. A preceding row was deleted, a row was
    /// reordered, or a row was inserted out of band, so the link does not point at its real
    /// predecessor.
    /// </summary>
    BrokenLink = 2,

    /// <summary>
    /// A row in the middle of an otherwise-hashed stream is missing its <see cref="AuditLog.EntryHash"/>.
    /// Hashing is append-only, so an unhashed row appearing after hashed rows indicates the hashed
    /// suffix was tampered with (for example a hashed row replaced by an unhashed forgery).
    /// </summary>
    MissingHashAfterChainStart = 3,
}

/// <summary>
/// Outcome of an <see cref="IAuditIntegrityVerifier.VerifyChainAsync"/> call. Either the verified
/// scope is intact (<see cref="IsValid"/> true), or it reports the first row at which the chain
/// broke and why. "First break" is by chain order, so the earliest tampering is surfaced rather than
/// an arbitrary one.
/// </summary>
public sealed class AuditChainVerificationResult
{
    private AuditChainVerificationResult(
        bool isValid,
        long verifiedRowCount,
        Guid? brokenAtId,
        string? brokenEntityType,
        string? brokenEntityId,
        AuditChainBreakReason reason,
        string? detail)
    {
        IsValid = isValid;
        VerifiedRowCount = verifiedRowCount;
        BrokenAtId = brokenAtId;
        BrokenEntityType = brokenEntityType;
        BrokenEntityId = brokenEntityId;
        Reason = reason;
        Detail = detail;
    }

    /// <summary>True when every hashed row in the verified scope chained intact.</summary>
    public bool IsValid { get; }

    /// <summary>
    /// Number of hashed rows that were checked and passed before the result was produced. On a
    /// valid result this is the full count of hashed rows verified; on a break it is the number of
    /// rows that passed before the offending one.
    /// </summary>
    public long VerifiedRowCount { get; }

    /// <summary>The <see cref="AuditLog.Id"/> of the first row that failed verification, or null when valid.</summary>
    public Guid? BrokenAtId { get; }

    /// <summary>The <see cref="AuditLog.EntityType"/> of the offending row, or null when valid.</summary>
    public string? BrokenEntityType { get; }

    /// <summary>The <see cref="AuditLog.EntityId"/> of the offending row, or null when valid.</summary>
    public string? BrokenEntityId { get; }

    /// <summary>The kind of break detected, or <see cref="AuditChainBreakReason.None"/> when valid.</summary>
    public AuditChainBreakReason Reason { get; }

    /// <summary>A human-readable description of the break, or null when valid.</summary>
    public string? Detail { get; }

    /// <summary>Builds a success result reporting how many hashed rows were verified.</summary>
    public static AuditChainVerificationResult Valid(long verifiedRowCount)
        => new(true, verifiedRowCount, null, null, null, AuditChainBreakReason.None, null);

    /// <summary>Builds a failure result pinpointing the first broken row and the reason.</summary>
    public static AuditChainVerificationResult Broken(
        long verifiedRowCount,
        AuditLog brokenRow,
        AuditChainBreakReason reason,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(brokenRow);
        return new AuditChainVerificationResult(
            false, verifiedRowCount, brokenRow.Id, brokenRow.EntityType, brokenRow.EntityId, reason, detail);
    }
}

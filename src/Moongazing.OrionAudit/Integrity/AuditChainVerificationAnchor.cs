namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// The persisted stream head a chain walk is checked against: the expected tail hash, the stream's
/// lifetime hashed-row count, and the retention prune checkpoint. This is verification <em>input</em>,
/// not a persistence row - it is what a caller asserts the stream should look like, so
/// <see cref="AuditChainVerifier.VerifyStream"/> can tell a legitimately pruned stream from a
/// truncated one.
/// </summary>
/// <remarks>
/// <para>
/// This type exists so the verifier stays honest for backends OrionAudit does not own. The chain is
/// keyed precisely so it can be verified without trusting the process that wrote it, which means an
/// out-of-tree <c>IAuditHistoryStore</c> - Mongo, Dynamo, an append-only file, a warehouse table -
/// must be able to hand the verifier its stream head. Its head does not live in an EF entity, so the
/// verifier does not ask for one: it asks for the values, and this is the shape they arrive in. An
/// EF-backed caller that already holds the mapped row converts with
/// <see cref="AuditChainAnchor.ToVerificationAnchor"/>.
/// </para>
/// <para>
/// It is deliberately <em>not</em> the mapped <see cref="AuditChainAnchor"/> entity. That entity is
/// the library's own record of a chain it wrote, and nothing outside the library should be able to
/// author one - an anchor that claims a tail hash the chain never had is a forged alibi for a
/// tampered stream. Verification input and persistence are different jobs, and keeping them in
/// different types means the distinction is enforced by the compiler rather than by a naming
/// convention: this value has no EF mapping, so constructing one can inform a verdict but can never
/// be saved as the library's own answer to a later one.
/// </para>
/// <para>
/// Every field the verifier actually reads is a required constructor argument, and the constructor
/// rejects a combination that cannot describe a real stream. A half-populated anchor is the one
/// failure this type must not permit: an anchor that omits the row count would still verify, would
/// still report success, and would silently have skipped truncation detection entirely - the exact
/// "a check that looks like it ran and did not" shape the anchor exists to prevent.
/// </para>
/// </remarks>
public sealed class AuditChainVerificationAnchor
{
    /// <summary>
    /// Records a stream head to verify against.
    /// </summary>
    /// <param name="entityType">Assembly-qualified entity type of the stream. Reported on a
    /// whole-stream-deletion result, which has no surviving row to point at.</param>
    /// <param name="entityId">Entity primary key of the stream, in canonical <see cref="AuditKey"/>
    /// form. Reported alongside <paramref name="entityType"/>.</param>
    /// <param name="latestEntryHash">The <see cref="AuditLog.EntryHash"/> of the stream's most recent
    /// hashed row. The walked tail must end here.</param>
    /// <param name="rowCount">The stream's <em>lifetime</em> hashed-row total, including rows
    /// retention has since pruned. Required, and required to be positive: an anchor only exists once a
    /// stream has been chained, so there is no such thing as one describing zero hashed rows, and
    /// accepting zero would quietly disarm the truncation check for an emptied stream.</param>
    /// <param name="prunedRowCount">How many of <paramref name="rowCount"/> the retention sweep
    /// legitimately removed from the head of the chain. Defaults to zero - never pruned.</param>
    /// <param name="prunedThroughHash">The <see cref="AuditLog.EntryHash"/> of the newest pruned row,
    /// which the oldest surviving row must link back to. Defaults to null - never pruned.</param>
    /// <exception cref="ArgumentException"><paramref name="entityType"/>, <paramref name="entityId"/>
    /// or <paramref name="latestEntryHash"/> is null or blank, or the prune checkpoint is incoherent.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="rowCount"/> is not positive, or
    /// <paramref name="prunedRowCount"/> is negative or exceeds <paramref name="rowCount"/>.</exception>
    public AuditChainVerificationAnchor(
        string entityType,
        string entityId,
        string latestEntryHash,
        long rowCount,
        long prunedRowCount = 0,
        string? prunedThroughHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(latestEntryHash);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(prunedRowCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prunedRowCount, rowCount);

        // The prune checkpoint is a pair, and retention only ever writes both halves together. Half of
        // it is not a lesser truth, it is a wrong one: a count with no watermark makes the oldest
        // survivor look like a genesis that should link to nothing, and a watermark with no count
        // makes an intact stream look short. Either way verification reports a break on a stream
        // nobody touched, so the incoherent pair is refused at the door rather than turned into a
        // verdict.
        if (prunedRowCount > 0 != (prunedThroughHash is not null))
        {
            throw new ArgumentException(
                $"The retention prune checkpoint is incomplete: {nameof(prunedRowCount)} is " +
                $"{prunedRowCount} while {nameof(prunedThroughHash)} is " +
                $"{(prunedThroughHash is null ? "null" : "set")}. Supply both (a pruned stream) or " +
                "neither (a stream retention has never pruned).",
                nameof(prunedThroughHash));
        }

        EntityType = entityType;
        EntityId = entityId;
        LatestEntryHash = latestEntryHash;
        RowCount = rowCount;
        PrunedRowCount = prunedRowCount;
        PrunedThroughHash = prunedThroughHash;
    }

    /// <summary>Assembly-qualified entity type of the stream.</summary>
    public string EntityType { get; }

    /// <summary>Entity primary key of the stream.</summary>
    public string EntityId { get; }

    /// <summary>The expected <see cref="AuditLog.EntryHash"/> of the stream's most recent hashed row.</summary>
    public string LatestEntryHash { get; }

    /// <summary>The stream's lifetime hashed-row total, pruned rows included.</summary>
    public long RowCount { get; }

    /// <summary>How many hashed rows retention legitimately removed from the head of the chain.</summary>
    public long PrunedRowCount { get; }

    /// <summary>
    /// The <see cref="AuditLog.EntryHash"/> the oldest surviving row must link back to, or null while
    /// the stream has never been pruned.
    /// </summary>
    public string? PrunedThroughHash { get; }
}

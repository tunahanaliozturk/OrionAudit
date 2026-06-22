namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Pure, backend-agnostic verification of a single entity stream's tamper-evident keyed-MAC chain.
/// Reflection-free and side-effect-free so it is shared by every store backend and trivially
/// unit-testable. The database read lives in <see cref="EfCoreAuditIntegrityVerifier"/>; this engine
/// only walks an already-ordered row list and (optionally) checks it against the stream's anchor.
/// </summary>
public static class AuditChainVerifier
{
    /// <summary>
    /// The collaborators a stream walk needs that live outside the row list: how to resolve the MAC
    /// key for a row's <see cref="AuditLog.HashKeyId"/>, how to read a row's registered custom-column
    /// values, and (optionally) the persisted anchor that proves no tail rows were deleted.
    /// </summary>
    public readonly record struct StreamVerificationContext(
        Func<int, ReadOnlyMemory<byte>?> KeyResolver,
        Func<AuditLog, IReadOnlyList<KeyValuePair<string, string?>>> CustomColumnsResolver,
        AuditChainAnchor? Anchor);

    /// <summary>
    /// Verifies one stream's chain. <paramref name="orderedRows"/> MUST be the stream's rows in
    /// canonical chain order: ascending (<see cref="AuditLog.OccurredOnUtc"/>, <see cref="AuditLog.Id"/>),
    /// which is the same order the stamper chained them in. When
    /// <paramref name="context"/>.<see cref="StreamVerificationContext.Anchor"/> is supplied, the walked
    /// tail hash and hashed-row count are checked against it so deleting the tail row(s) - or the whole
    /// stream - is detected even though the surviving prefix links intact.
    /// </summary>
    /// <param name="orderedRows">The stream's rows, oldest first.</param>
    /// <param name="context">Key/custom-column resolvers and the optional anchor.</param>
    /// <param name="alreadyVerified">Running count of rows verified in prior streams, folded into the
    /// returned result's <see cref="AuditChainVerificationResult.VerifiedRowCount"/> so a whole-table
    /// walk reports a cumulative total.</param>
    /// <returns>A valid result (with the cumulative verified count), or the first broken link.</returns>
    public static AuditChainVerificationResult VerifyStream(
        IReadOnlyList<AuditLog> orderedRows,
        StreamVerificationContext context,
        long alreadyVerified = 0)
    {
        ArgumentNullException.ThrowIfNull(orderedRows);
        ArgumentNullException.ThrowIfNull(context.KeyResolver);
        ArgumentNullException.ThrowIfNull(context.CustomColumnsResolver);

        long verified = alreadyVerified;
        long thisStream = 0;
        var chainStarted = false;
        string? expectedPreviousHash = null;
        AuditLog? lastHashedRow = null;

        foreach (var row in orderedRows)
        {
            if (!chainStarted)
            {
                if (row.EntryHash is null)
                {
                    // Still in the unhashed prefix written before chaining was enabled. Skip; this
                    // row is outside the verifiable scope.
                    continue;
                }
                // First hashed row of the stream: the genesis. Its predecessor is null by
                // construction (the stamper had no persisted head to chain onto), regardless of any
                // unhashed prefix before it.
                chainStarted = true;
                expectedPreviousHash = null;
            }
            else if (row.EntryHash is null)
            {
                // An unhashed row appearing AFTER the chain started is not a legitimate prefix:
                // hashing is append-only, so this is a tampered/forged row in the hashed suffix.
                return AuditChainVerificationResult.Broken(
                    verified, row, AuditChainBreakReason.MissingHashAfterChainStart,
                    $"Row '{row.Id}' has no EntryHash but follows hashed rows in stream " +
                    $"({row.EntityType}/{row.EntityId}); the hashed history was altered.");
            }

            // Link check first: does this row point at the row that actually precedes it? A deleted
            // or reordered predecessor makes PreviousHash disagree with the real predecessor's hash.
            if (!string.Equals(row.PreviousHash, expectedPreviousHash, StringComparison.Ordinal))
            {
                return AuditChainVerificationResult.Broken(
                    verified, row, AuditChainBreakReason.BrokenLink,
                    $"Row '{row.Id}' PreviousHash does not match the preceding row's EntryHash in " +
                    $"stream ({row.EntityType}/{row.EntityId}); a row was deleted, reordered, or inserted.");
            }

            // Resolve the key version this row was MAC'd with. An unregistered key id cannot be
            // recomputed, so the row's integrity is unprovable - surface it rather than skip it.
            var keyId = row.HashKeyId ?? 0;
            var key = context.KeyResolver(keyId);
            if (key is null)
            {
                return AuditChainVerificationResult.Broken(
                    verified, row, AuditChainBreakReason.UnknownKey,
                    $"Row '{row.Id}' references hash key id {keyId}, which is not registered with the " +
                    $"configured key provider; its MAC cannot be verified.");
            }

            // Content check: recompute this row's MAC from its content + custom columns + its
            // (now-validated) link. A mismatch means the row's own content was altered after capture.
            var recomputed = AuditEntryHasher.ComputeEntryHash(
                row, row.PreviousHash, key.Value.Span, context.CustomColumnsResolver(row));
            if (!string.Equals(recomputed, row.EntryHash, StringComparison.Ordinal))
            {
                return AuditChainVerificationResult.Broken(
                    verified, row, AuditChainBreakReason.ContentMismatch,
                    $"Row '{row.Id}' content does not match its stored EntryHash in stream " +
                    $"({row.EntityType}/{row.EntityId}); the row was modified after capture.");
            }

            expectedPreviousHash = row.EntryHash;
            lastHashedRow = row;
            verified++;
            thisStream++;
        }

        // Truncation guard: the surviving rows link intact, but the anchor remembers the stream's true
        // tail and length. If they disagree, tail rows (or the whole stream) were deleted.
        if (context.Anchor is { } anchor)
        {
            var truncation = CheckAnchor(anchor, lastHashedRow, thisStream, verified);
            if (truncation is not null)
            {
                return truncation;
            }
        }

        return AuditChainVerificationResult.Valid(verified);
    }

    private static AuditChainVerificationResult? CheckAnchor(
        AuditChainAnchor anchor, AuditLog? lastHashedRow, long thisStreamVerified, long cumulativeVerified)
    {
        // Whole-stream deletion: the anchor says the stream had rows, but none survive in scope.
        if (lastHashedRow is null)
        {
            return AuditChainVerificationResult.BrokenAt(
                cumulativeVerified, anchor.EntityType, anchor.EntityId, AuditChainBreakReason.Truncated,
                $"Stream ({anchor.EntityType}/{anchor.EntityId}) has no surviving hashed rows but its " +
                $"anchor records {anchor.RowCount}; the stream was deleted.");
        }

        // Tail deletion: a consistent prefix survives, but it is shorter than the anchor's count or its
        // tail hash is not the anchored latest hash.
        if (thisStreamVerified != anchor.RowCount
            || !string.Equals(lastHashedRow.EntryHash, anchor.LatestEntryHash, StringComparison.Ordinal))
        {
            return AuditChainVerificationResult.Broken(
                cumulativeVerified, lastHashedRow, AuditChainBreakReason.Truncated,
                $"Stream ({anchor.EntityType}/{anchor.EntityId}) tail disagrees with its anchor " +
                $"(walked {thisStreamVerified} row(s) ending {Short(lastHashedRow.EntryHash)}, anchor " +
                $"records {anchor.RowCount} ending {Short(anchor.LatestEntryHash)}); tail rows were deleted.");
        }

        return null;
    }

    private static string Short(string? hash)
        => hash is null ? "(null)" : hash.Length <= 12 ? hash : hash[..12];
}

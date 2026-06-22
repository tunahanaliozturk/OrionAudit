namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Pure, backend-agnostic verification of a single entity stream's tamper-evident hash chain.
/// Reflection-free and side-effect-free so it is shared by every store backend and trivially
/// unit-testable. The database read lives in <see cref="EfCoreAuditIntegrityVerifier"/>; this engine
/// only walks an already-ordered row list.
/// </summary>
public static class AuditChainVerifier
{
    /// <summary>
    /// Verifies one stream's chain. <paramref name="orderedRows"/> MUST be the stream's rows in
    /// canonical chain order: ascending (<see cref="AuditLog.OccurredOnUtc"/>, <see cref="AuditLog.Id"/>),
    /// which is the same order the stamper chained them in.
    /// </summary>
    /// <param name="orderedRows">The stream's rows, oldest first.</param>
    /// <param name="alreadyVerified">Running count of rows verified in prior streams, folded into the
    /// returned result's <see cref="AuditChainVerificationResult.VerifiedRowCount"/> so a whole-table
    /// walk reports a cumulative total.</param>
    /// <returns>A valid result (with the cumulative verified count), or the first broken link.</returns>
    public static AuditChainVerificationResult VerifyStream(
        IReadOnlyList<AuditLog> orderedRows,
        long alreadyVerified = 0)
    {
        ArgumentNullException.ThrowIfNull(orderedRows);

        long verified = alreadyVerified;
        var chainStarted = false;
        string? expectedPreviousHash = null;

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

            // Content check: recompute this row's hash from its content + its (now-validated) link.
            // A mismatch means the row's own content was altered after it was written.
            var recomputed = AuditEntryHasher.ComputeEntryHash(row, row.PreviousHash);
            if (!string.Equals(recomputed, row.EntryHash, StringComparison.Ordinal))
            {
                return AuditChainVerificationResult.Broken(
                    verified, row, AuditChainBreakReason.ContentMismatch,
                    $"Row '{row.Id}' content does not match its stored EntryHash in stream " +
                    $"({row.EntityType}/{row.EntityId}); the row was modified after capture.");
            }

            expectedPreviousHash = row.EntryHash;
            verified++;
        }

        return AuditChainVerificationResult.Valid(verified);
    }
}

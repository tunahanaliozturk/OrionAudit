using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Capture;

/// <summary>
/// Evaluates the periodic <see cref="SnapshotPolicy"/> against the <see cref="SnapshotCursor"/>
/// companion table. Shared by the synchronous interceptor and the async dispatcher so both
/// reach the same snapshot decision.
/// </summary>
internal static class SnapshotPolicyEvaluator
{
    /// <summary>
    /// Returns true when the supplied audit row should also carry a full snapshot, advancing
    /// (and lazily creating) the entity's <see cref="SnapshotCursor"/>. Must be called inside
    /// the same transaction that writes the resulting rows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Concurrency: <c>SnapshotEvery(n)</c> is approximate, with no guaranteed interval.</b>
    /// <see cref="SnapshotCursor.UpdatesSinceLast"/> is read, incremented in memory, and written
    /// back as an absolute value, with no lock and no concurrency token. With no concurrent writes
    /// to the same entity that is exact: every save reads what the previous one wrote, and every
    /// n-th update snapshots. Under concurrent writes to one entity stream
    /// (<see cref="SnapshotCursor.EntityType"/> + <see cref="SnapshotCursor.EntityId"/> +
    /// <see cref="SnapshotCursor.TenantId"/>) the cadence is approximate in <em>both</em>
    /// directions, and no interval is promised either way:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>Snapshots can land closer together than <c>n</c>.</b> Two saves that both read
    /// <c>n - 1</c> both decide to snapshot, so two snapshots can be a single update apart.
    /// </item>
    /// <item>
    /// <b>The counter can move backwards, so a snapshot can be delayed without bound.</b> The write
    /// is a blind overwrite, not an increment: A reads 0, B commits 1, C commits 2, then A commits
    /// its stale 1 and the count has gone down. Sustained contention can repeat that indefinitely,
    /// so there is no upper bound on how long a snapshot is deferred — not the counter's value
    /// times anything, not any function of how many writers overlap.
    /// </item>
    /// </list>
    /// <para>
    /// The value stays sane even so: it is only ever written as some reader's value plus one, or
    /// reset to 0 by a snapshot, so it never goes negative and never persists above <c>n - 1</c>.
    /// </para>
    /// <para>
    /// <b>What is guaranteed regardless.</b> Everything about the audit trail itself. No row is
    /// lost, delayed, or mis-stamped; the diff chain stays complete; <c>AuditReconstructor</c> can
    /// always rebuild any state at any point. <see cref="AuditLog.Snapshot"/> is purely a
    /// replay-cost optimisation, so a cadence that drifts costs replay time and nothing else.
    /// </para>
    /// <para>
    /// <b>The bounded variant is <c>SnapshotEvery(TimeSpan)</c>.</b> It keys off
    /// <see cref="SnapshotCursor.LastSnapshotUtc"/> rather than a counter, and that field is only
    /// ever written as some save's own <c>occurredOn</c> — so it can never be set to a time further
    /// ahead than a save that really happened. A stale write can only move it <em>earlier</em>,
    /// which makes the window expire sooner rather than later. Concurrency therefore shows up there
    /// as an extra snapshot, never as a late one, and the interval is not exceeded (absent clock
    /// skew between capture hosts). Consumers who need a cadence they can rely on should use it.
    /// </para>
    /// <para>
    /// <b>Why the counter is not guarded.</b> A concurrency token on the cursor would make the
    /// losing writer raise <see cref="DbUpdateConcurrencyException"/> — but the cursor is written
    /// inside the consumer's <c>SaveChanges</c>, so that exception aborts the consumer's business
    /// transaction and names a table they never asked for. Failing a customer's order save to keep
    /// a snapshot cadence exact is the wrong trade, and it would contradict how the rest of capture
    /// behaves: a diff failure annotates the row, a custom-column provider failure annotates the
    /// row, and an observer fault is swallowed. Making the write monotonic instead was considered
    /// and is not reachable from here either: EF's change tracker emits absolute values, no EF
    /// transaction exists yet at interceptor time (so any relative SQL we issue ourselves would
    /// commit separately and count rolled-back saves), and the one conditional shape EF does
    /// support — a predicate on the old value — is the concurrency token again, because EF checks
    /// rows-affected and throws when the stale writer matches none. Note also that monotonicity
    /// alone would not restore a lower bound: two savers that both read <c>n - 1</c> both decide to
    /// snapshot however the write is performed.
    /// </para>
    /// <para>
    /// <b>Known edge.</b> Two saves that are both the <em>first</em> update to one entity insert
    /// that entity's cursor row concurrently, and the composite primary key rejects the second with
    /// a <see cref="DbUpdateException"/>. That predates this analysis and is not introduced by it.
    /// </para>
    /// <para>
    /// The cursor is read with <c>FindAsync</c>: this runs on the caller's <c>SavingChangesAsync</c>
    /// hot path, where a synchronous round-trip blocks a thread-pool thread for every save. Once
    /// the cursor is tracked, later saves on the same context resolve it from the change tracker
    /// and never touch the database again.
    /// </para>
    /// </remarks>
    public static async Task<bool> ShouldSnapshotAsync(
        DbContext ctx,
        SnapshotPolicy policy,
        AuditLog row,
        DateTime occurredOn,
        CancellationToken cancellationToken)
    {
        var cursor = await ctx.Set<SnapshotCursor>()
            .FindAsync([row.EntityType, row.EntityId, row.TenantId ?? string.Empty], cancellationToken)
            .ConfigureAwait(false);
        if (cursor is null)
        {
            cursor = new SnapshotCursor
            {
                EntityType = row.EntityType,
                EntityId = row.EntityId,
                TenantId = row.TenantId ?? string.Empty,
                UpdatesSinceLast = 0,
                LastSnapshotUtc = null,
            };
            ctx.Add(cursor);
        }

        cursor.UpdatesSinceLast++;
        var shouldSnapshot = policy switch
        {
            SnapshotPolicy.EveryNthPolicy n => cursor.UpdatesSinceLast >= n.Updates,
            SnapshotPolicy.EveryDurationPolicy d =>
                cursor.LastSnapshotUtc is null
                || (occurredOn - cursor.LastSnapshotUtc.Value) >= d.Elapsed,
            _ => false,
        };

        if (shouldSnapshot)
        {
            cursor.UpdatesSinceLast = 0;
            cursor.LastSnapshotUtc = occurredOn;
        }
        return shouldSnapshot;
    }
}

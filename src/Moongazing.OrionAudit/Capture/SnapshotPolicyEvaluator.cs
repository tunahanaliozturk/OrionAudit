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
    public static bool ShouldSnapshot(DbContext ctx, SnapshotPolicy policy, AuditLog row, DateTime occurredOn)
    {
        var cursor = ctx.Set<SnapshotCursor>().Find(row.EntityType, row.EntityId, row.TenantId ?? string.Empty);
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

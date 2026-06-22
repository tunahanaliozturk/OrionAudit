using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// <see cref="IAuditIntegrityVerifier"/> backed by the consumer's <see cref="DbContext"/>. Reads the
/// audit rows in chain order, recomputes each row's keyed MAC, and checks each stream against its
/// persisted <see cref="AuditChainAnchor"/> so tail-row (or whole-stream) deletion is detected. Read-
/// only with respect to the database (it never calls <c>SaveChanges</c>), so it is safe to run on a
/// live table or a replica. This is the default verifier registered by the DI wiring when
/// hash-chaining is enabled.
/// </summary>
public sealed class EfCoreAuditIntegrityVerifier : IAuditIntegrityVerifier
{
    private readonly DbContext context;
    private readonly IAuditChainKeyProvider keyProvider;
    private readonly IReadOnlyList<CustomColumn> customColumns;

    /// <summary>
    /// Initializes a new instance reading from <paramref name="context"/>, resolving MAC keys via
    /// <paramref name="keyProvider"/> and binding the registered <paramref name="customColumns"/> into
    /// each row's MAC (so a tampered custom column is detected).
    /// </summary>
    public EfCoreAuditIntegrityVerifier(
        DbContext context,
        IAuditChainKeyProvider keyProvider,
        IReadOnlyList<CustomColumn> customColumns)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        this.keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        this.customColumns = customColumns ?? throw new ArgumentNullException(nameof(customColumns));
    }

    /// <inheritdoc />
    public async Task<AuditChainVerificationResult> VerifyChainAsync(
        AuditChainVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        // Resolve the stream keys in scope. A single-entity request is one stream; a broader request
        // expands to the distinct (EntityType, EntityId, TenantId) streams that carry at least one
        // hashed row OR have an anchor (so a whole-stream deletion - rows gone, anchor remaining - is
        // still surfaced rather than silently dropped).
        var streams = await ResolveStreamsAsync(request, cancellationToken).ConfigureAwait(false);

        var keyResolver = BuildKeyResolver();

        long verified = 0;
        foreach (var stream in streams)
        {
            var anchor = await LoadAnchorAsync(stream, cancellationToken).ConfigureAwait(false);
            var rows = await LoadStreamAsync(request, stream, cancellationToken).ConfigureAwait(false);

            var ctx = new AuditChainVerifier.StreamVerificationContext(
                keyResolver, BuildCustomColumnsResolver(), anchor);
            var result = AuditChainVerifier.VerifyStream(rows, ctx, verified);
            if (!result.IsValid)
            {
                return result;
            }
            verified = result.VerifiedRowCount;

            // Bound the change tracker when rows were loaded tracked (custom-column case).
            if (customColumns.Count > 0)
            {
                context.ChangeTracker.Clear();
            }
        }

        return AuditChainVerificationResult.Valid(verified);
    }

    private Func<int, ReadOnlyMemory<byte>?> BuildKeyResolver()
        => keyId => keyProvider.TryGetKey(keyId);

    // When no custom columns are registered every row's bound set is empty, so a single shared empty
    // list avoids per-row allocation. With custom columns, values come from the tracked entity's
    // shadow properties (the row must therefore be loaded tracked - see LoadStreamAsync).
    private Func<AuditLog, IReadOnlyList<KeyValuePair<string, string?>>> BuildCustomColumnsResolver()
    {
        if (customColumns.Count == 0)
        {
            var empty = Array.Empty<KeyValuePair<string, string?>>();
            return _ => empty;
        }

        return row =>
        {
            var entry = context.Entry(row);
            var values = new List<KeyValuePair<string, string?>>(customColumns.Count);
            foreach (var column in customColumns)
            {
                var value = entry.Property(column.Name).CurrentValue;
                values.Add(new KeyValuePair<string, string?>(
                    column.Name, CustomColumnCanonicalizer.ToCanonicalString(value)));
            }
            return values;
        };
    }

    private async Task<List<StreamKey>> ResolveStreamsAsync(
        AuditChainVerificationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EntityType is not null && request.EntityId is not null)
        {
            return new List<StreamKey>
            {
                new(request.EntityType, request.EntityId, request.TenantId ?? string.Empty),
            };
        }

        // Streams that have hashed rows.
        var rowQuery = context.Set<AuditLog>().AsNoTracking().Where(a => a.EntryHash != null);
        if (request.EntityType is not null)
        {
            rowQuery = rowQuery.Where(a => a.EntityType == request.EntityType);
        }
        if (request.TenantId is not null)
        {
            rowQuery = rowQuery.Where(a => a.TenantId == request.TenantId);
        }
        var rowStreams = await rowQuery
            .Select(a => new { a.EntityType, a.EntityId, TenantId = a.TenantId ?? string.Empty })
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Streams that have an anchor (covers whole-stream deletion: rows gone, anchor present).
        var anchorQuery = context.Set<AuditChainAnchor>().AsNoTracking().AsQueryable();
        if (request.EntityType is not null)
        {
            anchorQuery = anchorQuery.Where(a => a.EntityType == request.EntityType);
        }
        if (request.TenantId is not null)
        {
            anchorQuery = anchorQuery.Where(a => a.TenantId == request.TenantId);
        }
        // Coalesce TenantId to "" exactly as the row projection above does. Anchors written after the
        // write-path normalization already store ""; this also folds any historic anchor that physically
        // stored null. Critically it keeps StreamKey.TenantId non-null (so LoadStreamAsync's
        // stream.TenantId.Length cannot NRE) AND makes the no-tenant anchor key-equal to the no-tenant
        // row key, so the union below dedupes them into ONE stream instead of verifying it twice.
        // (?? maps to SQL COALESCE; an arbitrary helper call would not translate, hence not AuditTenant
        // here - the canonical value is identical.)
        var anchorStreams = await anchorQuery
            .Select(a => new { a.EntityType, a.EntityId, TenantId = a.TenantId ?? string.Empty })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Union (deterministic order so a whole-table verify reports a stable first break across runs).
        var set = new SortedSet<StreamKey>(StreamKeyComparer.Instance);
        foreach (var s in rowStreams)
        {
            set.Add(new StreamKey(s.EntityType, s.EntityId, s.TenantId));
        }
        foreach (var s in anchorStreams)
        {
            set.Add(new StreamKey(s.EntityType, s.EntityId, s.TenantId));
        }
        return set.ToList();
    }

    private async Task<AuditChainAnchor?> LoadAnchorAsync(StreamKey stream, CancellationToken cancellationToken)
    {
        // No-tracking: the anchor is read-only context for the walk and we never persist it here.
        var query = context.Set<AuditChainAnchor>()
            .AsNoTracking()
            .Where(a => a.EntityType == stream.EntityType && a.EntityId == stream.EntityId);

        // Match the no-tenant stream against BOTH a null and an empty-string anchor, mirroring exactly
        // how LoadStreamAsync scopes the rows. New anchors persist "" (write-path normalization), but a
        // historic anchor may physically store null; without the null branch that anchor would not be
        // found for the "" stream key and the stream's truncation/whole-deletion check would silently
        // pass (deleted tail undetected). A concrete tenant matches precisely so a shared entity id
        // across tenants loads exactly one tenant's anchor.
        query = stream.TenantId.Length == 0
            ? query.Where(a => a.TenantId == null || a.TenantId == "")
            : query.Where(a => a.TenantId == stream.TenantId);

        return await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<AuditLog>> LoadStreamAsync(
        AuditChainVerificationRequest request,
        StreamKey stream,
        CancellationToken cancellationToken)
    {
        var query = context.Set<AuditLog>()
            .Where(a => a.EntityType == stream.EntityType && a.EntityId == stream.EntityId);

        // Tenant scoping mirrors compaction. The stream key already carries a concrete tenant (empty
        // string for the null-tenant stream); match it precisely so a shared entity id under multiple
        // tenants verifies exactly one tenant's chain. A null TenantId column maps to the empty-string
        // stream.
        query = stream.TenantId.Length == 0
            ? query.Where(a => a.TenantId == null || a.TenantId == "")
            : query.Where(a => a.TenantId == stream.TenantId);

        var ordered = query.OrderBy(a => a.OccurredOnUtc).ThenBy(a => a.Id);

        // With custom columns the MAC binds shadow-property values, which are only reachable through a
        // tracked entry, so load tracked in that case (the tracker is cleared per stream by the
        // caller). With no custom columns, no-tracking keeps the walk allocation-light.
        return customColumns.Count == 0
            ? await ordered.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)
            : await ordered.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private readonly record struct StreamKey(string EntityType, string EntityId, string TenantId);

    private sealed class StreamKeyComparer : IComparer<StreamKey>
    {
        public static readonly StreamKeyComparer Instance = new();

        public int Compare(StreamKey x, StreamKey y)
        {
            var byType = string.CompareOrdinal(x.EntityType, y.EntityType);
            if (byType != 0)
            {
                return byType;
            }
            var byId = string.CompareOrdinal(x.EntityId, y.EntityId);
            return byId != 0 ? byId : string.CompareOrdinal(x.TenantId, y.TenantId);
        }
    }
}

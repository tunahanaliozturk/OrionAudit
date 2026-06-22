using Microsoft.EntityFrameworkCore;

namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// <see cref="IAuditIntegrityVerifier"/> backed by the consumer's <see cref="DbContext"/>. Reads the
/// audit rows in chain order and walks each entity stream through the pure
/// <see cref="AuditChainVerifier"/>. Read-only: it never writes, so it is safe to run on a live
/// table or a replica. This is the default verifier registered by the DI wiring when hash-chaining
/// is enabled.
/// </summary>
public sealed class EfCoreAuditIntegrityVerifier : IAuditIntegrityVerifier
{
    private readonly DbContext context;

    /// <summary>Initializes a new instance reading from the supplied <see cref="DbContext"/>.</summary>
    public EfCoreAuditIntegrityVerifier(DbContext context)
        => this.context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public async Task<AuditChainVerificationResult> VerifyChainAsync(
        AuditChainVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        // Resolve the stream keys in scope. A single-entity request is one stream; a broader request
        // expands to the distinct (EntityType, EntityId) pairs that carry at least one hashed row, so
        // unhashed-only entities are skipped entirely rather than walked to a trivial valid result.
        var streams = await ResolveStreamsAsync(request, cancellationToken).ConfigureAwait(false);

        long verified = 0;
        foreach (var stream in streams)
        {
            var rows = await LoadStreamAsync(request, stream.EntityType, stream.EntityId, cancellationToken)
                .ConfigureAwait(false);

            var result = AuditChainVerifier.VerifyStream(rows, verified);
            if (!result.IsValid)
            {
                return result;
            }
            verified = result.VerifiedRowCount;
        }

        return AuditChainVerificationResult.Valid(verified);
    }

    private async Task<List<(string EntityType, string EntityId)>> ResolveStreamsAsync(
        AuditChainVerificationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.EntityType is not null && request.EntityId is not null)
        {
            return new List<(string, string)> { (request.EntityType, request.EntityId) };
        }

        var query = context.Set<AuditLog>().AsNoTracking().Where(a => a.EntryHash != null);
        if (request.EntityType is not null)
        {
            query = query.Where(a => a.EntityType == request.EntityType);
        }
        if (request.TenantId is not null)
        {
            query = query.Where(a => a.TenantId == request.TenantId);
        }

        // Deterministic stream order so a whole-table verify reports a stable "first break" across
        // runs. The per-stream walk itself orders by (OccurredOnUtc, Id).
        var keys = await query
            .Select(a => new { a.EntityType, a.EntityId })
            .Distinct()
            .OrderBy(k => k.EntityType)
            .ThenBy(k => k.EntityId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return keys.ConvertAll(k => (k.EntityType, k.EntityId));
    }

    private async Task<List<AuditLog>> LoadStreamAsync(
        AuditChainVerificationRequest request,
        string entityType,
        string entityId,
        CancellationToken cancellationToken)
    {
        var query = context.Set<AuditLog>().AsNoTracking()
            .Where(a => a.EntityType == entityType && a.EntityId == entityId);

        // Tenant scoping mirrors compaction: when a tenant is requested only that tenant's rows
        // participate, so a shared entity id under multiple tenants verifies one tenant's chain.
        if (request.TenantId is not null)
        {
            query = query.Where(a => a.TenantId == request.TenantId);
        }

        return await query
            .OrderBy(a => a.OccurredOnUtc)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

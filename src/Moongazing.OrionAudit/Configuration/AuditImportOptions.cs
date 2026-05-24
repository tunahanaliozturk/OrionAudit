namespace Moongazing.OrionAudit.Configuration;

/// <summary>
/// Tunables passed to <c>DbContext.CreateAuditImport(o =&gt; ...)</c>. The
/// <see cref="ImportBatch"/> string is REQUIRED before <c>SaveAsync</c> — it drives
/// idempotency by stamping <c>AuditLog.CorrelationId</c>.
/// </summary>
public sealed class AuditImportOptions
{
    private int batchSize = 1000;
    private string? importBatch;

    /// <summary>How many rows the importer writes per transaction. Default: 1000.</summary>
    public int BatchSize
    {
        get => batchSize;
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(BatchSize), value, "Must be >= 1.");
            }
            batchSize = value;
        }
    }

    /// <summary>
    /// Stable, per-import label. Stamped into <c>AuditLog.CorrelationId</c> as
    /// <c>import:{ImportBatch}#{SourceId}</c> so re-runs are idempotent.
    /// Required; <c>SaveAsync</c> throws if null.
    /// </summary>
    public string? ImportBatch
    {
        get => importBatch;
        set
        {
            if (value is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
            }
            importBatch = value;
        }
    }
}

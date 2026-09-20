using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Moongazing.OrionAudit.Integrity;

/// <summary>
/// Opens (and releases) the explicit transaction the hash chain's write path needs, for the two
/// callers that stamp a chain: the capture interceptor and the async dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EfCoreAuditHashChainWriter"/> takes a pessimistic row lock on each touched stream's
/// <see cref="AuditChainAnchor"/> before reading its head. That lock only serializes while it is
/// <em>held</em>, and a statement that joins no transaction releases it the moment it returns - before
/// the anchor has even been read. Neither write path had a transaction open at that point: a plain
/// <c>SaveChanges</c> has none at interceptor time (EF opens its own <em>after</em> the interceptor
/// runs), and the dispatcher never opened one. So two concurrent same-stream appends both read the
/// same head, both stamped it as their <c>PreviousHash</c>, and both committed - and verification then
/// reported <c>BrokenLink</c> on a trail nobody had touched.
/// </para>
/// <para>
/// Opening the transaction here, before the lock, puts the lock, the head read, the stamped rows and
/// the advanced anchor in one unit, which is the serialization <see cref="AuditChainAnchor"/>
/// documents. A transaction the consumer opened themselves is left alone - it already spans the
/// stamp, and committing someone else's transaction is not ours to do.
/// </para>
/// <para>
/// A provider with no transaction support (the EF in-memory provider) raises
/// <see cref="InvalidOperationException"/> on begin; that is caught and the work runs unwrapped rather
/// than crashing, the same degradation <see cref="Retention.CopyToTableAuditArchiver{TArchiveRow}"/>
/// and <see cref="Retention.ChainPruneArchiver"/> already use.
/// </para>
/// </remarks>
internal static class ChainWriteTransaction
{
    /// <summary>
    /// Throws when this context is configured with a <em>retrying</em> execution strategy and nobody
    /// has opened a transaction, because OrionAudit cannot open one there and cannot make the save
    /// retriable on the consumer's behalf either. Call this from a write path that does NOT own the
    /// <c>SaveChanges</c> call (the capture interceptor); a path that owns it runs the whole unit
    /// through the strategy instead and must not call this.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EF Core refuses user-initiated transactions inside a retriable unit
    /// (<c>ExecutionStrategy.OnFirstExecution</c> throws <see cref="InvalidOperationException"/>), and
    /// it enters that unit inside <c>SaveChanges</c> - after the interceptor has already run. So a
    /// transaction opened while capturing exists before the strategy starts, and every hash-chained
    /// save on a context with <c>EnableRetryOnFailure()</c> would fail with EF's message, which says
    /// nothing about OrionAudit.
    /// </para>
    /// <para>
    /// The unit cannot be made retriable from an interceptor: the <c>SaveChanges</c> call belongs to
    /// the consumer, and only its owner can wrap it. Their one-line change makes it work and is the
    /// same thing EF requires of any explicit transaction under a retrying strategy, so this throws
    /// with that snippet rather than silently dropping the transaction - which would hand back the
    /// unserialized chain this whole mechanism exists to prevent.
    /// </para>
    /// </remarks>
    /// <exception cref="OrionAuditConfigurationException">A retrying execution strategy is configured
    /// and no transaction is ambient.</exception>
    public static void EnsureCanOpenTransaction(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Database.CurrentTransaction is not null)
        {
            return;
        }

        IExecutionStrategy strategy;
        try
        {
            strategy = context.Database.CreateExecutionStrategy();
        }
        catch (InvalidOperationException)
        {
            // Non-relational providers (the EF in-memory provider) have no strategy to ask; they also
            // have no transactions, so BeginOrNullAsync degrades on its own.
            return;
        }

        if (!strategy.RetriesOnFailure)
        {
            return;
        }

        throw new OrionAuditConfigurationException(
            $"OrionAudit's hash chain stamps audit rows inside a write transaction, but this DbContext "
            + $"is configured with the retrying execution strategy '{strategy.GetType().Name}', and EF Core "
            + "does not allow a transaction to be opened inside a retriable unit by anything other than "
            + "the code that owns the SaveChanges call. OrionAudit's interceptor does not own it, so it "
            + "cannot wrap the save for you."
            + Environment.NewLine + Environment.NewLine
            + "Own the transaction at your call site and the chain stamps inside it:"
            + Environment.NewLine + Environment.NewLine
            + "    var strategy = db.Database.CreateExecutionStrategy();" + Environment.NewLine
            + "    await strategy.ExecuteAsync(async () =>" + Environment.NewLine
            + "    {" + Environment.NewLine
            + "        await using var transaction = await db.Database.BeginTransactionAsync();" + Environment.NewLine
            + "        await db.SaveChangesAsync();" + Environment.NewLine
            + "        await transaction.CommitAsync();" + Environment.NewLine
            + "    });" + Environment.NewLine + Environment.NewLine
            + "Otherwise remove the retrying strategy from this context (the EnableRetryOnFailure() call "
            + "on its provider options), or turn off UseHashChain().");
    }

    /// <summary>
    /// Begins the chain's write transaction on <paramref name="context"/>, or returns
    /// <see langword="null"/> when one is already ambient (the consumer owns it) or the provider has
    /// no transaction support.
    /// </summary>
    public static async Task<IDbContextTransaction?> BeginOrNullAsync(
        DbContext context, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is not null)
        {
            return null;
        }

        try
        {
            return await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Commits or rolls back a transaction from <see cref="BeginOrNullAsync"/> and disposes it.
    /// A <see langword="null"/> transaction is a no-op, so callers do not branch.
    /// </summary>
    public static async Task ReleaseAsync(IDbContextTransaction? transaction, bool commit)
    {
        if (transaction is null)
        {
            return;
        }

        try
        {
            // CancellationToken.None: the release decision has already been made, and abandoning a
            // half-released transaction because the caller's token tripped is strictly worse than
            // finishing it.
            if (commit)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // a failing rollback must not replace the failure that caused it
        catch (Exception) when (!commit)
#pragma warning restore CA1031
        {
            // The connection may already have been torn down by whatever aborted the save; disposing
            // below still releases it, and the original exception is the one worth surfacing.
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }
}

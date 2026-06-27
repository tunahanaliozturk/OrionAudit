using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Moongazing.OrionAudit.Store;

/// <summary>
/// Streams audit history for a filter out of an <see cref="IAuditHistoryStore"/> as NDJSON
/// (newline-delimited JSON: one compact JSON object per line). Built for large result sets: it walks
/// the store one bounded page at a time and writes each page straight to the destination, so the whole
/// history is never held in memory at once. Suitable for feeding a warehouse, a SIEM, or a file.
/// </summary>
/// <remarks>
/// <para>
/// The exporter drives <see cref="IAuditHistoryStore.QueryAsync"/> with a forced
/// <see cref="AuditHistoryOrder.OldestFirst"/> ordering and an internal page size, advancing
/// <see cref="AuditHistoryQuery.Skip"/> by the page size each round until a short (or empty) page is
/// reached. Oldest-first gives a deterministic, append-friendly export order (a downstream consumer can
/// resume from the last seen row) and a stable paging walk.
/// </para>
/// <para>
/// Because paging reads each page in a separate store call, a concurrent write during a long export can
/// shift later pages; the export is a best-effort point-in-time-ish snapshot, not a serializable one.
/// For an exact snapshot, export against a read replica or a quiesced window.
/// </para>
/// </remarks>
public sealed class AuditHistoryExporter
{
    /// <summary>Default rows per internal page when the caller does not override it. Keeps each store round-trip bounded.</summary>
    public const int DefaultPageSize = 500;

    private readonly IAuditHistoryStore store;

    /// <summary>Initializes a new exporter over <paramref name="store"/>.</summary>
    public AuditHistoryExporter(IAuditHistoryStore store)
        => this.store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Streams the rows matching <paramref name="filter"/> oldest-first, one bounded page at a time, as
    /// an async sequence. The caller is never handed more than <paramref name="pageSize"/> rows in
    /// memory at once. The filter's <see cref="AuditHistoryQuery.Skip"/>, <see cref="AuditHistoryQuery.Take"/>,
    /// and <see cref="AuditHistoryQuery.Order"/> are managed by the exporter and ignored on input.
    /// </summary>
    /// <param name="filter">Which rows to export (entity / subject / action / time-range filters).</param>
    /// <param name="pageSize">Rows per internal page. Defaults to <see cref="DefaultPageSize"/>. Must be at least 1.</param>
    /// <param name="cancellationToken">Cancellation token; checked between pages and rows.</param>
    public async IAsyncEnumerable<AuditLog> StreamRowsAsync(
        AuditHistoryQuery filter,
        int pageSize = DefaultPageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Page size must be at least 1.");
        }

        var skip = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Re-derive the page query from the caller's FILTERS only, overriding paging/order so the
            // walk is the exporter's, not the caller's. `with` keeps the record's filter fields intact.
            var pageQuery = filter with
            {
                Skip = skip,
                Take = pageSize,
                Order = AuditHistoryOrder.OldestFirst,
            };

            var page = await store.QueryAsync(pageQuery, cancellationToken).ConfigureAwait(false);
            var count = page.Items.Count;
            if (count == 0)
            {
                yield break;
            }

            foreach (var row in page.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }

            // A short page is the last page: the store had fewer than a full page left. Stop without an
            // extra round-trip that would return an empty page.
            if (count < pageSize)
            {
                yield break;
            }

            skip += count;
        }
    }

    /// <summary>
    /// Writes the rows matching <paramref name="filter"/> as NDJSON to <paramref name="destination"/>,
    /// streaming page-by-page (the full result is never buffered). Each row is one compact JSON object
    /// followed by a single <c>\n</c>. An empty result writes nothing and returns. The stream is flushed
    /// after each page so a downstream reader sees rows as they are produced; the destination is not
    /// closed or disposed.
    /// </summary>
    /// <param name="destination">The output stream (a file, a network stream, an HTTP response body).</param>
    /// <param name="filter">Which rows to export.</param>
    /// <param name="pageSize">Rows per internal page. Defaults to <see cref="DefaultPageSize"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows written.</returns>
    public async Task<long> ExportNdjsonAsync(
        Stream destination,
        AuditHistoryQuery filter,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(filter);

        // One writer reused across the whole export; Reset() rebinds it to the buffer between rows so we
        // emit one object per line. SkipValidation is safe: WriteRow produces well-formed objects.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        await using var jsonWriter = new Utf8JsonWriter(
            buffer, new JsonWriterOptions { SkipValidation = true });
        var newline = "\n"u8.ToArray();

        long written = 0;
        var sinceFlush = 0;
        await foreach (var row in StreamRowsAsync(filter, pageSize, cancellationToken).ConfigureAwait(false))
        {
            buffer.Clear();
            jsonWriter.Reset(buffer);
            AuditNdjsonWriter.WriteRow(jsonWriter, row);
            jsonWriter.Flush();

            await destination.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(newline, cancellationToken).ConfigureAwait(false);
            written++;

            // Flush the destination roughly per page so a slow consumer sees steady progress without a
            // syscall per row.
            if (++sinceFlush >= pageSize)
            {
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                sinceFlush = 0;
            }
        }

        if (written > 0)
        {
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        return written;
    }
}

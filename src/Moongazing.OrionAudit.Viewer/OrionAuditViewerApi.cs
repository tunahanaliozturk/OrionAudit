using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Read;

namespace Moongazing.OrionAudit.Viewer;

/// <summary>Maps the viewer's read-only JSON API onto a route group.</summary>
internal static class OrionAuditViewerApi
{
    public static void Map<TDbContext>(RouteGroupBuilder group)
        where TDbContext : DbContext
    {
        // Paged recent audit rows. [FromServices] is required on the DbContext and on the
        // interface-typed services so the minimal-API binder does not infer them as bodies.
        group.MapGet("/api/log", async (
            [FromServices] TDbContext db,
            [FromServices] IAuditConfiguration config,
            int? page, int? size) =>
        {
            var take = Math.Clamp(size is null or <= 0 ? 50 : size.Value, 1, 500);
            var skip = Math.Max((page ?? 1) - 1, 0) * take;
            var rows = await db.AuditLog()
                .OrderByDescending(a => a.OccurredOnUtc)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
            var views = rows.Select(r => AuditViewRenderer.Render(r, ProjectCustoms(db, r, config))).ToList();
            return Results.Ok(new { entries = views });
        });

        // One entity's chronological timeline.
        group.MapGet("/api/{entityType}/{key}", async (
            [FromServices] TDbContext db,
            [FromServices] IAuditConfiguration config,
            string entityType, string key) =>
        {
            var rows = await db.AuditLog()
                .Where(a => a.EntityType == entityType && a.EntityId == key)
                .OrderBy(a => a.OccurredOnUtc)
                .ToListAsync();
            var views = rows.Select(r => AuditViewRenderer.Render(r, ProjectCustoms(db, r, config))).ToList();
            return Results.Ok(new { entries = views });
        });

        // Audited type names + (in async mode) the capture-queue depth + custom-column names.
        group.MapGet("/api/meta",
            async ([FromServices] IAuditConfiguration config,
                   [FromServices] IAuditDispatcher dispatcher) =>
        {
            var queueDepth = await dispatcher.GetQueueDepthAsync();
            return Results.Ok(new
            {
                auditedTypes = config.AuditedTypeNames,
                queueDepth,
                customColumnNames = config.CustomColumns.Select(c => c.Name).ToArray(),
            });
        });
    }

    // EF tracks the loaded row, so reading shadow values is an in-memory access — no extra
    // round-trip per row. Empty dictionary when no columns registered.
    private static Dictionary<string, object?> ProjectCustoms(
        DbContext db, AuditLog row, IAuditConfiguration config)
    {
        if (config.CustomColumns.Count == 0)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
        var entry = db.Entry(row);
        var result = new Dictionary<string, object?>(config.CustomColumns.Count, StringComparer.Ordinal);
        foreach (var column in config.CustomColumns)
        {
            result[column.Name] = entry.Property(column.Name).CurrentValue;
        }
        return result;
    }
}

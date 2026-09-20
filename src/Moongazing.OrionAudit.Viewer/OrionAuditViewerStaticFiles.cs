using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Read;

namespace Moongazing.OrionAudit.Viewer;

/// <summary>
/// Serves the viewer's embedded single-page UI at the route-group root, with the most recent
/// audit entries rendered into it server-side.
/// </summary>
/// <remarks>
/// Audited values are attacker-controlled: anyone who can write to an audited entity chooses
/// what an admin later reads in this page. Every value is therefore HTML-encoded on output.
/// The rendering deliberately lives here rather than in the browser - the page used to build
/// its markup with <c>innerHTML</c> from the JSON API, which executed those values in the
/// reviewing admin's session.
/// </remarks>
internal static class OrionAuditViewerStaticFiles
{
    /// <summary>Marker in the embedded shell that the rendered entry markup replaces.</summary>
    private const string EntriesPlaceholder = "<!--orionaudit:entries-->";

    /// <summary>Rows shown on the page; matches the JSON API's default page size.</summary>
    private const int PageSize = 50;

    private static readonly Lazy<string> Shell = new(LoadHtml);

    public static void Map<TDbContext>(RouteGroupBuilder group)
        where TDbContext : DbContext
    {
        // Root of the route group ("/audit" -> "" relative to the prefix). [FromServices] is
        // required so the minimal-API binder does not infer the parameters as request bodies.
        group.MapGet("/", async (
            [FromServices] TDbContext db,
            [FromServices] IAuditConfiguration config) =>
        {
            var rows = await db.AuditLog()
                .OrderByDescending(a => a.OccurredOnUtc)
                .Take(PageSize)
                .ToListAsync();
            var views = rows
                .Select(r => AuditViewRenderer.Render(r, config, OrionAuditViewerApi.ProjectCustoms(db, r, config)))
                .ToList();
            var html = Shell.Value.Replace(EntriesPlaceholder, RenderEntries(views), StringComparison.Ordinal);
            return Results.Content(html, "text/html");
        });
    }

    // Builds the entry list markup. Every interpolated value is an HTML *text node* - class
    // names and element names are literals, and no audit value reaches an attribute, a
    // <script> block, or a JSON island - so the HTML encoder is the correct encoder at every
    // site here. HtmlEncoder.Default also escapes quotes and non-ASCII, so a value that later
    // moves into an attribute context stays safe.
    private static string RenderEntries(List<AuditEntryView> entries)
    {
        if (entries.Count == 0)
        {
            return "No audit entries yet.";
        }

        var html = new StringBuilder(entries.Count * 256);
        foreach (var entry in entries)
        {
            html.Append("<div class=\"entry\"><div class=\"entry-head\">")
                .Append("<span class=\"action\">").Append(Encode(entry.Action.ToString())).Append("</span>")
                .Append("<span>").Append(Encode(entry.OccurredOnUtc.ToString("u", CultureInfo.InvariantCulture))).Append("</span>")
                .Append("<span>").Append(Encode(entry.UserDisplay ?? "-")).Append("</span>");

            foreach (var column in entry.CustomColumns)
            {
                if (column.Value is null)
                {
                    continue;
                }
                html.Append("<span class=\"badge\">")
                    .Append(Encode(column.Key))
                    .Append(": ")
                    .Append(Encode(Convert.ToString(column.Value, CultureInfo.InvariantCulture) ?? string.Empty))
                    .Append("</span>");
            }
            html.Append("</div>");

            foreach (var change in entry.Changes)
            {
                html.Append("<div class=\"change\">")
                    .Append("<span class=\"path\">").Append(Encode(change.DisplayLabel ?? change.PropertyPath)).Append("</span>")
                    .Append("<span class=\"kind\">").Append(Encode(change.ChangeKind.ToString())).Append("</span>")
                    .Append("<span><span class=\"old\">").Append(Encode(change.OldValue ?? string.Empty)).Append("</span> ")
                    .Append("<span class=\"new\">").Append(Encode(change.NewValue ?? string.Empty)).Append("</span></span>")
                    .Append("</div>");
            }
            html.Append("</div>");
        }
        return html.ToString();
    }

    private static string Encode(string value) => HtmlEncoder.Default.Encode(value);

    private static string LoadHtml()
    {
        var asm = typeof(OrionAuditViewerStaticFiles).Assembly;
        // EmbeddedResource logical name: <RootNamespace>.wwwroot.index.html
        var name = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith("wwwroot.index.html", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Embedded viewer index.html not found.");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

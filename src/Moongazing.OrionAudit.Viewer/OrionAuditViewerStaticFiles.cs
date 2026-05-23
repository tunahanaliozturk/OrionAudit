using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Moongazing.OrionAudit.Viewer;

/// <summary>Serves the viewer's embedded single-page UI at the route-group root.</summary>
internal static class OrionAuditViewerStaticFiles
{
    private static readonly Lazy<string> Html = new(LoadHtml);

    public static void Map(RouteGroupBuilder group)
    {
        // Root of the route group ("/audit" → "" relative to the prefix).
        group.MapGet("/", () => Results.Content(Html.Value, "text/html"));
    }

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

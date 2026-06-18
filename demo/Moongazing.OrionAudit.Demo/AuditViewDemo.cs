using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Read;

namespace Moongazing.OrionAudit.Demo;

/// <summary>
/// Feature 4 - Rendering a human-readable audit view.
/// Turns the raw AuditLog rows from the time-travel trail into AuditEntryView
/// objects via the pure AuditViewRenderer, surfacing field-level changes and the
/// consumer-friendly display labels declared in the configuration.
/// </summary>
internal static class AuditViewDemo
{
    public static void Run(AuditTrail trail)
    {
        DemoSupport.Banner("4. AUDIT VIEW - render rows into a readable change history");

        // The labelled overload resolves "Status" -> "Workflow Status", etc.
        var views = trail.Rows
            .OrderBy(r => r.OccurredOnUtc)
            .Select(r => AuditViewRenderer.Render(r, DemoSupport.Configuration))
            .ToList();

        foreach (var view in views)
        {
            DemoSupport.Section($"{view.OccurredOnUtc:u}  {view.Action,-11}  by {view.UserDisplay}");
            Console.WriteLine($"  entity: {view.EntityDisplayLabel ?? "Order"}   correlation: {view.CorrelationId}");
            if (view.Changes.Count == 0)
            {
                Console.WriteLine("  (no field-level changes)");
                continue;
            }
            foreach (var change in view.Changes)
            {
                var label = change.DisplayLabel ?? change.PropertyPath;
                Console.WriteLine($"  [{change.ChangeKind,-8}] {label} = {change.NewValue ?? "<removed>"}");
            }
        }
    }
}

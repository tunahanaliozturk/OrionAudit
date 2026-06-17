using System.Text.Json.Nodes;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Read;

namespace Moongazing.OrionAudit.Demo;

/// <summary>
/// Feature 5 - Step-by-step replay (the reconstructed state at each step).
/// Walks the trail one row at a time, applying each diff and printing the running
/// state after every event. This is the "movie" version of reconstruction: instead
/// of jumping to a single point in time it shows the entity evolving frame by frame.
/// </summary>
internal static class ReplayStepDemo
{
    public static void Run(AuditTrail trail)
    {
        DemoSupport.Banner("5. STEP REPLAY - reconstructed state after each event");

        var ordered = trail.Rows.OrderBy(r => r.OccurredOnUtc).ToList();
        JsonObject state = new();

        for (var i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];

            // Anchor rows (Insert) carry a full snapshot; start from it. Otherwise
            // apply the diff onto the running state, exactly like the reconstructor.
            if (i == 0 && row.Snapshot is not null)
            {
                state = JsonNode.Parse(row.Snapshot)!.AsObject();
            }
            else if (!string.IsNullOrEmpty(row.Diff) && row.Diff != "[]")
            {
                state = DiffEngine.Apply(state, row.Diff);
            }

            var changeSummary = string.Join(", ",
                AuditViewRenderer.Render(row).Changes
                    .Select(c => $"{c.PropertyPath.TrimStart('/')}={c.NewValue ?? "-"}"));

            DemoSupport.Section($"step {i + 1}: {row.Action} ({(changeSummary.Length == 0 ? "no change" : changeSummary)})");
            Console.WriteLine($"  Status={Field(state, "Status")}  Total={Field(state, "Total")}  Customer={Field(state, "Customer")}");
            Console.WriteLine($"  Email(hashed)={Truncate(Field(state, "Email"))}  ApiKey={Field(state, "ApiKey")}");
        }
    }

    private static string Field(JsonObject state, string name) =>
        state.TryGetPropertyValue(name, out var v) && v is not null ? v.ToString() : "<absent>";

    private static string Truncate(string s) => s.Length <= 16 ? s : s[..16] + "...";
}

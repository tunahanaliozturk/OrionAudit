using System.Text.Json;
using System.Text.Json.Nodes;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.Demo;

/// <summary>
/// Feature 3 - Time-travel reconstruction.
/// Records a full lifecycle (insert, two updates, soft-delete) into an in-memory
/// trail, then reconstructs the entity state "as of" each point in time, proving
/// that a sequence of diffs is enough to rebuild any historical state.
/// </summary>
internal static class TimeTravelDemo
{
    public static (AuditTrail Trail, List<DateTime> Marks) Run()
    {
        DemoSupport.Banner("3. TIME-TRAVEL - reconstruct state from a sequence of events");

        var id = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var start = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        var trail = new AuditTrail(id, start);

        var order = new Order
        {
            Id = id,
            Status = "Pending",
            Total = 100.00m,
            Customer = "Acme Corp",
            Email = "buyer@acme.example",
            ApiKey = "sk_live_x",
        };
        trail.Capture(order, AuditAction.Inserted, "alice");

        order.Status = "Paid";
        order.Total = 110.00m;
        trail.Capture(order, AuditAction.Updated, "billing-job");

        order.Status = "Shipped";
        order.Customer = "Acme Corporation";
        trail.Capture(order, AuditAction.Updated, "bob");

        order.Status = "Cancelled";
        trail.Capture(order, AuditAction.SoftDeleted, "alice");

        // One time mark just after each captured row.
        var marks = trail.Rows.Select(r => r.OccurredOnUtc.AddMinutes(1)).ToList();
        var labels = new[] { "after insert", "after payment", "after shipment", "after soft-delete" };

        for (var i = 0; i < marks.Count; i++)
        {
            DemoSupport.Section($"Reconstruct as of {marks[i]:u}  ({labels[i]})");
            var state = trail.ReconstructAsOf(marks[i]);
            if (state is null)
            {
                Console.WriteLine("  -> entity did not exist / was deleted at this instant (null)");
            }
            else
            {
                Console.WriteLine($"  Status   = {Field(state, "Status")}");
                Console.WriteLine($"  Total    = {Field(state, "Total")}");
                Console.WriteLine($"  Customer = {Field(state, "Customer")}");
            }
        }

        DemoSupport.Section("Reconstruct before the entity existed");
        var before = trail.ReconstructAsOf(start);
        Console.WriteLine($"  -> {(before is null ? "null (correct - nothing to replay yet)" : "unexpected state")}");

        return (trail, marks);
    }

    private static string Field(JsonObject state, string name) =>
        state.TryGetPropertyValue(name, out var v) && v is not null
            ? v.ToJsonString()
            : "<absent>";
}

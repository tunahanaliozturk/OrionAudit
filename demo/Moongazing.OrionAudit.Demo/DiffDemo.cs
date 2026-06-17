using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Demo;

/// <summary>
/// Feature 2 - Diffing two states (RFC 6902 JSON Patch).
/// Mutates an order between two snapshots and shows the compact JSON Patch the
/// in-house diff engine emits, then proves the patch is replayable: applying it
/// to the "before" snapshot reproduces the "after" snapshot byte-for-byte.
/// </summary>
internal static class DiffDemo
{
    public static void Run()
    {
        DemoSupport.Banner("2. DIFF - RFC 6902 JSON Patch between two states");

        var order = new Order
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Status = "Pending",
            Total = 100.00m,
            Customer = "Acme Corp",
            Email = "buyer@acme.example",
            ApiKey = "sk_live_x",
        };
        var before = DemoSupport.Snapshot(order);

        // Mutate the aggregate.
        order.Status = "Shipped";
        order.Total = 120.00m;
        order.Customer = "Acme Corporation";
        var after = DemoSupport.Snapshot(order);

        var patch = DiffEngine.Compute(before, after);

        DemoSupport.Section("Diff (this is what gets stored in AuditLog.Diff)");
        Console.WriteLine("  " + patch);

        DemoSupport.Section("Replay check - apply the patch back onto 'before'");
        var rebuilt = DiffEngine.Apply(before, patch);
        var roundTrips = System.Text.Json.Nodes.JsonNode.DeepEquals(rebuilt, after);
        Console.WriteLine($"  Apply(before, patch) == after ? {roundTrips}");

        DemoSupport.Section("Empty diff when nothing changed");
        Console.WriteLine("  " + DiffEngine.Compute(after, after) + "  (no-op changes write [])");
    }
}

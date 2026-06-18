using System.Text.Json;
using System.Text.Json.Nodes;

namespace Moongazing.OrionAudit.Demo;

/// <summary>
/// Feature 1 - Snapshotting with field rules.
/// Builds a JSON snapshot of an entity, demonstrating how [HashedAudit],
/// [RedactedAudit] and [NotAuditable] reshape what actually lands in the trail.
/// </summary>
internal static class SnapshotDemo
{
    public static void Run()
    {
        DemoSupport.Banner("1. SNAPSHOT - build an audit snapshot from an entity");

        var order = new Order
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Status = "Pending",
            Total = 129.90m,
            Customer = "Acme Corp",
            Email = "buyer@acme.example",
            ApiKey = "sk_live_super_secret_value",
            InternalNote = "do-not-audit-this",
        };

        Console.WriteLine("Live entity:");
        Console.WriteLine($"  Email        = {order.Email}");
        Console.WriteLine($"  ApiKey       = {order.ApiKey}");
        Console.WriteLine($"  InternalNote = {order.InternalNote}");

        var snapshot = DemoSupport.Snapshot(order);

        DemoSupport.Section("Snapshot actually written to the audit trail");
        Console.WriteLine(Pretty(snapshot));

        DemoSupport.Section("What the field rules did");
        Console.WriteLine("  Email        -> SHA-256 hex (deterministic, no plaintext leak)");
        Console.WriteLine("  ApiKey       -> literal \"<redacted>\"");
        Console.WriteLine("  InternalNote -> omitted entirely (key is absent above)");
    }

    private static string Pretty(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
}

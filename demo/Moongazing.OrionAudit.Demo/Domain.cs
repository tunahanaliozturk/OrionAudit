using System.Text.Json.Nodes;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Demo;

/// <summary>
/// A toy audited aggregate used across every demo. The attributes drive the
/// snapshot field rules exactly as they would inside a real EF Core SaveChanges,
/// but here everything runs purely in memory.
/// </summary>
[Auditable]
public sealed class Order
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "";
    public decimal Total { get; set; }
    public string Customer { get; set; } = "";

    /// <summary>Stored as a SHA-256 hex hash in the audit trail, never as plaintext.</summary>
    [HashedAudit] public string Email { get; set; } = "";

    /// <summary>Stored as the literal "&lt;redacted&gt;" in the audit trail.</summary>
    [RedactedAudit] public string ApiKey { get; set; } = "";

    /// <summary>Never written to the audit trail at all.</summary>
    [NotAuditable] public string InternalNote { get; set; } = "";

    public Order Clone() => new()
    {
        Id = Id,
        Status = Status,
        Total = Total,
        Customer = Customer,
        Email = Email,
        ApiKey = ApiKey,
        InternalNote = InternalNote,
    };
}

/// <summary>
/// In-memory helpers shared by the demos. These intentionally mirror what the
/// EF Core interceptor and reconstructor do internally, but without any database:
/// build a snapshot from an entity, turn a snapshot into an AuditLog row, and
/// pretty-print to the console.
/// </summary>
internal static class DemoSupport
{
    /// <summary>
    /// A single audit configuration reused everywhere. Declares the field rules
    /// (Hash / Redact / Exclude) and human-readable labels for the viewer demo.
    /// </summary>
    public static IAuditConfiguration Configuration { get; } =
        new AuditConfigurationBuilder()
            .Audit<Order>(b => b
                .Label("Sales Order")
                .Label(o => o.Total, "Order Total")
                .Label(o => o.Status, "Workflow Status")
                .Hash(o => o.Email)
                .Redact(o => o.ApiKey)
                .Exclude(o => o.InternalNote))
            .Build();

    /// <summary>
    /// Reflects the public properties of an entity into the property-bag shape that
    /// <see cref="Capture.SnapshotBuilder.Build(Type, IReadOnlyDictionary{string, object?}, IAuditConfiguration)"/>
    /// expects. In a real app EF Core supplies this bag from its change tracker.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> PropertyBag(object entity)
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in entity.GetType().GetProperties())
        {
            if (prop.GetIndexParameters().Length != 0 || prop.GetMethod is null)
            {
                continue;
            }
            bag[prop.Name] = prop.GetValue(entity);
        }
        return bag;
    }

    /// <summary>Builds an audit snapshot (field rules applied) for an entity.</summary>
    public static JsonObject Snapshot(object entity) =>
        Capture.SnapshotBuilder.Build(entity.GetType(), PropertyBag(entity), Configuration);

    public static void Banner(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine("  " + title);
        Console.WriteLine(new string('=', 72));
    }

    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("-- " + title);
    }
}

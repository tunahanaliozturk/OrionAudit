using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Configuration;
using Moongazing.OrionAudit.Probe;

// This probe exists solely to be Native-AOT published in CI. It exercises OrionAudit's
// reflection-free surface — SnapshotBuilder, DiffEngine, the fluent configuration builder,
// AuditKey, AuditScope, and the source-generated [OrionAuditModule] registration — WITHOUT
// touching the EF Core-coupled APIs (the interceptor, the reconstructor, ApplyOrionAudit-
// Configurations). EF Core itself is not AOT-compatible, so those paths can never be probed;
// what this proves is that OrionAudit's own code adds zero IL2*/IL3* warnings on top.

Console.WriteLine("OrionAudit AOT probe");

// 1. Source-generated module registration — no reflective assembly scan.
var builder = new AuditConfigurationBuilder();
ProbeAuditModule.RegisterAuditedTypes(builder);
var config = builder.Build();
Console.WriteLine($"  config.IsAudited(ProbeOrder) = {config.IsAudited(typeof(ProbeOrder))}");
Console.WriteLine($"  generator-discovered types  = {ProbeAuditModule.AuditedTypeNames.Count}");

// 2. SnapshotBuilder with a source-generated JsonSerializerContext — no reflective serialise.
var values = new Dictionary<string, object?>(StringComparer.Ordinal)
{
    ["Status"] = "Pending",
    ["Total"] = 42.5m,
    ["Tag"] = new ProbeTag { Label = "vip" },
};
var before = SnapshotBuilder.Build(typeof(ProbeOrder), values, config, ProbeJsonContext.Default);
values["Status"] = "Shipped";
var after = SnapshotBuilder.Build(typeof(ProbeOrder), values, config, ProbeJsonContext.Default);
Console.WriteLine($"  before snapshot keys        = {before.Count}");

// 3. DiffEngine compute + apply — pure JSON Patch.
var diff = DiffEngine.Compute(before, after);
var roundTrip = DiffEngine.Apply(before, diff);
Console.WriteLine($"  diff non-empty              = {diff.Length > 2}");
Console.WriteLine($"  apply round-trips           = {roundTrip["Status"]!.GetValue<string>() == "Shipped"}");

// 4. AuditKey composite-key serialisation.
var key = AuditKey.From("tenant-A", Guid.Empty, "en");
Console.WriteLine($"  composite key               = {key}");

// 5. AuditScope ambient correlation.
using (AuditScope.Push("aot-probe-run"))
{
    Console.WriteLine($"  ambient correlation         = {AuditScope.Current}");
}

Console.WriteLine("AOT probe complete — OrionAudit's reflection-free surface is clean.");

namespace Moongazing.OrionAudit.Probe
{
    [OrionAuditModule]
    internal partial class ProbeAuditModule { }

    [Auditable]
    internal sealed class ProbeOrder
    {
        public Guid Id { get; set; }
        public string Status { get; set; } = "";
        public decimal Total { get; set; }
    }

    internal sealed class ProbeTag
    {
        public string Label { get; set; } = "";
    }

    [JsonSerializable(typeof(ProbeOrder))]
    [JsonSerializable(typeof(ProbeTag))]
    internal partial class ProbeJsonContext : JsonSerializerContext { }
}

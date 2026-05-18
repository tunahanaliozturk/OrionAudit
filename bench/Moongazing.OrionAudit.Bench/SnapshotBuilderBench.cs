using BenchmarkDotNet.Attributes;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Bench;

/// <summary>
/// Measures SnapshotBuilder cost across (a) no audit rules, (b) all-capture, (c) heavy
/// hash/redact. Useful baseline because Snapshot is the hot path called per audited entity
/// per SaveChanges.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class SnapshotBuilderBench
{
    [Auditable]
    public sealed class Entity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Notes { get; set; } = "";
        public decimal Amount { get; set; }
        public DateTime Created { get; set; }
        public string Email { get; set; } = "";
        public string ApiKey { get; set; } = "";
    }

    private Dictionary<string, object?> values = null!;
    private IAuditConfiguration captureAll = null!;
    private IAuditConfiguration withRules = null!;

    [GlobalSetup]
    public void Setup()
    {
        values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Id"] = Guid.NewGuid(),
            ["Name"] = "Sample Entity",
            ["Notes"] = new string('x', 256),
            ["Amount"] = 1234.56m,
            ["Created"] = DateTime.UtcNow,
            ["Email"] = "user@example.com",
            ["ApiKey"] = "sk_live_supersecret_value_12345",
        };

        captureAll = new AuditConfigurationBuilder().Audit<Entity>().Build();
        withRules = new AuditConfigurationBuilder()
            .Audit<Entity>(b => b.Hash(e => e.Email).Redact(e => e.ApiKey))
            .Build();
    }

    [Benchmark(Baseline = true)]
    public object Build_AttributesOnly() => SnapshotBuilder.Build(typeof(Entity), values, captureAll);

    [Benchmark]
    public object Build_WithHashAndRedact() => SnapshotBuilder.Build(typeof(Entity), values, withRules);
}

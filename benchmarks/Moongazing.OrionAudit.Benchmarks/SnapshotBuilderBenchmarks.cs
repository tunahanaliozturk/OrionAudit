using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Benchmarks;

/// <summary>
/// Measures <see cref="SnapshotBuilder.Build(System.Type, System.Collections.Generic.IReadOnlyDictionary{string, object?}, IAuditConfiguration)"/>,
/// the per-entity snapshot hot path invoked once for every audited entity on every SaveChanges.
/// Pure and in-memory: no database, no DI container, no EF Core. Compares an attributes-only
/// capture against a configuration that hashes one field and redacts another, so the delta
/// isolates the cost of the SHA-256 hash path.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class SnapshotBuilderBenchmarks
{
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
    public object Build_AttributesOnly()
        => SnapshotBuilder.Build(typeof(Entity), values, captureAll);

    [Benchmark]
    public object Build_WithHashAndRedact()
        => SnapshotBuilder.Build(typeof(Entity), values, withRules);
}

using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Benchmarks;

/// <summary>
/// Measures the RFC 6902 diff engine, the workhorse of OrionAudit. <see cref="DiffEngine.Compute"/>
/// runs on write (once per audited entity per SaveChanges); <see cref="DiffEngine.Apply"/> runs on
/// read (once per audit row during time-travel reconstruction). Both paths are pure and in-memory
/// (JsonObject in, JSON string or JsonObject out) with no database involved. Parameterized by
/// property count to show how each path scales with snapshot width.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class DiffEngineBenchmarks
{
    [Params(4, 16, 64)]
    public int PropertyCount { get; set; }

    private JsonObject before = null!;
    private JsonObject after = null!;
    private string diff = null!;

    [GlobalSetup]
    public void Setup()
    {
        before = BuildSnapshot(PropertyCount, salt: 0);
        after = BuildSnapshot(PropertyCount, salt: 1);
        diff = DiffEngine.Compute(before, after);
    }

    [Benchmark(Baseline = true)]
    public string Compute() => DiffEngine.Compute(before, after);

    [Benchmark]
    public JsonObject Apply() => DiffEngine.Apply(before, diff);

    private static JsonObject BuildSnapshot(int count, int salt)
    {
        var node = new JsonObject();
        for (var i = 0; i < count; i++)
        {
            node[$"Prop{i}"] = $"value-{i}-{salt}";
        }
        return node;
    }
}

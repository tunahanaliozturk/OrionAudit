using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using OrionAudit.Capture;

namespace OrionAudit.Bench;

/// <summary>
/// Diff Compute / Apply costs across snapshot sizes. The Compute path runs once per audited
/// entity on every SaveChanges; Apply runs once per audit row during reconstruction.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class DiffEngineBench
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

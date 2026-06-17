using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Moongazing.OrionAudit.Capture;

namespace Moongazing.OrionAudit.Benchmarks;

/// <summary>
/// Measures the pure replay loop at the heart of time-travel reconstruction: folding a chain of
/// RFC 6902 patches into a starting snapshot via repeated <see cref="DiffEngine.Apply"/>. This is
/// the same fold the database-backed reconstructor performs once it has loaded the audit rows,
/// with the storage IO removed so the benchmark isolates the in-memory replay cost. Cost grows
/// with the number of events replayed, so it is parameterized by event count.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class ReplayBenchmarks
{
    [Params(10, 100, 1000)]
    public int EventCount { get; set; }

    private const int PropertyCount = 16;

    private JsonObject initial = null!;
    private string[] patches = null!;

    [GlobalSetup]
    public void Setup()
    {
        initial = BuildSnapshot(salt: 0);

        // Pre-compute the chain of diffs once: each event mutates the entity and yields the
        // RFC 6902 patch that the capture path would have persisted in AuditLog.Diff. Replay
        // then folds them back in, exactly as reconstruction does.
        patches = new string[EventCount];
        var current = initial;
        for (var i = 0; i < EventCount; i++)
        {
            var next = BuildSnapshot(salt: i + 1);
            patches[i] = DiffEngine.Compute(current, next);
            current = next;
        }
    }

    [Benchmark]
    public JsonObject Replay()
    {
        var state = initial;
        for (var i = 0; i < patches.Length; i++)
        {
            state = DiffEngine.Apply(state, patches[i]);
        }
        return state;
    }

    private static JsonObject BuildSnapshot(int salt)
    {
        var node = new JsonObject();
        for (var i = 0; i < PropertyCount; i++)
        {
            node[$"Prop{i}"] = $"value-{i}-{salt}";
        }
        return node;
    }
}

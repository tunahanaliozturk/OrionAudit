using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Moongazing.OrionAudit.Capture;
using Moongazing.OrionAudit.Read;

namespace Moongazing.OrionAudit.Benchmarks;

/// <summary>
/// Measures the read-side projection that turns persisted <see cref="AuditLog"/> rows into
/// human-readable <see cref="AuditEntryView"/>s. <see cref="AuditViewRenderer.Render(AuditLog)"/>
/// parses one row's RFC 6902 diff into field-level changes; <see cref="AuditViewRenderer.RenderMany"/>
/// orders and projects a page of rows. Pure and in-memory: depends only on System.Text.Json, with
/// no database. Parameterized by the number of rows rendered to show how the read path scales with
/// page size.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
[SimpleJob(RuntimeMoniker.Net90)]
public class AuditViewRenderBenchmarks
{
    [Params(1, 50, 500)]
    public int RowCount { get; set; }

    private AuditLog[] rows = null!;
    private AuditLog single = null!;

    [GlobalSetup]
    public void Setup()
    {
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        rows = new AuditLog[RowCount];
        for (var i = 0; i < RowCount; i++)
        {
            rows[i] = new AuditLog
            {
                EntityType = "Sample.Entity, Sample",
                EntityId = i.ToString(),
                Action = AuditAction.Updated,
                OccurredOnUtc = baseTime.AddSeconds(i),
                UserDisplay = "user@example.com",
                CorrelationId = Guid.NewGuid().ToString(),
                Diff = BuildDiff(propertyCount: 8, salt: i),
            };
        }

        single = rows[0];
    }

    [Benchmark]
    public AuditEntryView RenderOne() => AuditViewRenderer.Render(single);

    [Benchmark]
    public IReadOnlyList<AuditEntryView> RenderMany()
        => AuditViewRenderer.RenderMany(rows);

    private static string BuildDiff(int propertyCount, int salt)
    {
        var before = new JsonObject();
        var after = new JsonObject();
        for (var i = 0; i < propertyCount; i++)
        {
            before[$"Prop{i}"] = $"old-{i}";
            after[$"Prop{i}"] = $"new-{i}-{salt}";
        }
        return DiffEngine.Compute(before, after);
    }
}

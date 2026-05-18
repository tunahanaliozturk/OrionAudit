using BenchmarkDotNet.Running;
using OrionAudit.Bench;

BenchmarkSwitcher
    .FromTypes(new[]
    {
        typeof(SnapshotBuilderBench),
        typeof(DiffEngineBench),
        typeof(InterceptorBench),
        typeof(ReconstructorBench),
    })
    .Run(args);

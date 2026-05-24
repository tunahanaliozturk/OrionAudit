using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AuditLogCustomColumnMappingTests
{
    private sealed class MappingDb : DbContext
    {
        public MappingDb(DbContextOptions<MappingDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyOrionAuditConfigurations(customColumns: new[]
            {
                new CustomColumn("WorkflowStepId", typeof(int), _ => 0),
                new CustomColumn("Source", typeof(string), _ => null),
                new CustomColumn("RequestId", typeof(Guid?), _ => null),
            });
    }

    [Fact]
    public void CustomColumns_Are_NullableShadowProperties_With_RightClrType()
    {
        var opts = new DbContextOptionsBuilder<MappingDb>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new MappingDb(opts);
        var et = db.Model.FindEntityType(typeof(AuditLog))!;

        var step = et.FindProperty("WorkflowStepId")!;
        // Shadow properties materialise non-nullable value types as Nullable<T> when IsRequired(false).
        Assert.Equal(typeof(int?), step.ClrType);
        Assert.True(step.IsNullable);

        var source = et.FindProperty("Source")!;
        Assert.Equal(typeof(string), source.ClrType);
        Assert.True(source.IsNullable);
        Assert.Equal(512, source.GetMaxLength());

        var req = et.FindProperty("RequestId")!;
        Assert.Equal(typeof(Guid?), req.ClrType);
        Assert.True(req.IsNullable);
    }
}

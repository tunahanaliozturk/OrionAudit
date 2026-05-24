using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AuditConfigurationCustomColumnsTests
{
    private sealed class TestDb : DbContext
    {
        public TestDb(DbContextOptions<TestDb> options) : base(options) { }
    }

    [Fact]
    public void Configuration_Exposes_RegisteredCustomColumns()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<TestDb>(o => o
            .AddColumn<int>("WorkflowStepId", _ => 1)
            .AddColumn<string>("Source", _ => "x"));
        using var sp = services.BuildServiceProvider();

        var config = sp.GetRequiredService<IAuditConfiguration>();
        Assert.Equal(2, config.CustomColumns.Count);
        Assert.Contains(config.CustomColumns, c => c.Name == "WorkflowStepId" && c.ClrType == typeof(int));
        Assert.Contains(config.CustomColumns, c => c.Name == "Source" && c.ClrType == typeof(string));
    }

    [Fact]
    public void Configuration_NoCustomColumns_IsEmpty()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOrionAudit<TestDb>(o => { });
        using var sp = services.BuildServiceProvider();

        Assert.Empty(sp.GetRequiredService<IAuditConfiguration>().CustomColumns);
    }
}

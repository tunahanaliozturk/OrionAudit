using Microsoft.EntityFrameworkCore;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.Tests;

public class AuditCaptureQueueConfigurationTests
{
    private sealed class QueueDb : DbContext
    {
        public QueueDb(DbContextOptions<QueueDb> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyConfiguration(new AuditCaptureQueueEntityTypeConfiguration());
    }

    [Fact]
    public void Maps_To_DefaultTableName_With_LongIdentityKey()
    {
        var options = new DbContextOptionsBuilder<QueueDb>()
            .UseInMemoryDatabase("queue-cfg").Options;
        using var db = new QueueDb(options);
        var et = db.Model.FindEntityType(typeof(AuditCaptureQueueEntry))!;

        Assert.Equal("OrionAudit_Capture_Queue", et.GetTableName());
        var key = et.FindPrimaryKey()!;
        Assert.Single(key.Properties);
        Assert.Equal(nameof(AuditCaptureQueueEntry.Id), key.Properties[0].Name);
    }

    [Fact]
    public void DefaultTableName_Constant_IsStable()
        => Assert.Equal("OrionAudit_Capture_Queue", AuditCaptureQueueEntityTypeConfiguration.DefaultTableName);
}

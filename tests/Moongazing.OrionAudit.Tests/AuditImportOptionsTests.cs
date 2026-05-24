using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AuditImportOptionsTests
{
    [Fact]
    public void Defaults_AreSensible()
    {
        var o = new AuditImportOptions();
        Assert.Equal(1000, o.BatchSize);
        Assert.Null(o.ImportBatch);
    }

    [Fact]
    public void BatchSize_Rejects_NonPositive()
    {
        var o = new AuditImportOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => o.BatchSize = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => o.BatchSize = -1);
    }

    [Fact]
    public void ImportBatch_Rejects_NullOrWhitespace()
    {
        var o = new AuditImportOptions();
        Assert.Throws<ArgumentException>(() => o.ImportBatch = "");
        Assert.Throws<ArgumentException>(() => o.ImportBatch = "   ");
    }

    [Fact]
    public void ImportResult_DefaultsToZeros()
    {
        var r = new ImportResult(0, 0, 0);
        Assert.Equal(0, r.Written);
        Assert.Equal(0, r.Skipped);
        Assert.Equal(0, r.DeadLettered);
    }
}

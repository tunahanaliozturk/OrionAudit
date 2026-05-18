using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AuditableTypeDiscoveryTests
{
    [Auditable]
    public sealed class Marked
    {
        public int Id { get; set; }
    }

    public sealed class Unmarked
    {
        public int Id { get; set; }
    }

    [Auditable]
    public abstract class MarkedAbstract
    {
        public int Id { get; set; }
    }

    [Fact]
    public void Discover_FindsTypesWithAuditableAttribute()
    {
        var types = AuditableTypeDiscovery.Discover(new[] { typeof(AuditableTypeDiscoveryTests).Assembly });
        Assert.Contains(typeof(Marked), types);
    }

    [Fact]
    public void Discover_IgnoresUnmarkedTypes()
    {
        var types = AuditableTypeDiscovery.Discover(new[] { typeof(AuditableTypeDiscoveryTests).Assembly });
        Assert.DoesNotContain(typeof(Unmarked), types);
    }

    [Fact]
    public void Discover_IgnoresAbstractTypes()
    {
        var types = AuditableTypeDiscovery.Discover(new[] { typeof(AuditableTypeDiscoveryTests).Assembly });
        Assert.DoesNotContain(typeof(MarkedAbstract), types);
    }
}

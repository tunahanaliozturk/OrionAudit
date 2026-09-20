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

    // Abstract on purpose: the test below pins that reflective discovery skips it too, so OA0002
    // is the generator agreeing with the assertion.
#pragma warning disable OA0002 // [Auditable] type is abstract and is not registered
    [Auditable]
    public abstract class MarkedAbstract
    {
        public int Id { get; set; }
    }
#pragma warning restore OA0002

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

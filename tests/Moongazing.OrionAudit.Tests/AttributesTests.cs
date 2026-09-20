using System.Reflection;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.Tests;

public class AttributesTests
{
    // Private nested on purpose: this fixture only ever goes through reflection, never through a
    // generated module, so OA0003 is the generator being right about it.
#pragma warning disable OA0003 // [Auditable] type is not reachable from the generated module
    [Auditable]
    private sealed class Sample
    {
        public int Id { get; set; }
        [NotAuditable] public string Internal { get; set; } = "";
        [HashedAudit] public string Email { get; set; } = "";
        [RedactedAudit] public string Token { get; set; } = "";
    }
#pragma warning restore OA0003

    [Fact]
    public void Auditable_IsClassLevel_AndDetectable()
    {
        var attr = typeof(Sample).GetCustomAttribute<AuditableAttribute>();
        Assert.NotNull(attr);
    }

    [Fact]
    public void NotAuditable_HashedAudit_RedactedAudit_AreProperty_Level()
    {
        Assert.NotNull(typeof(Sample).GetProperty(nameof(Sample.Internal))!.GetCustomAttribute<NotAuditableAttribute>());
        Assert.NotNull(typeof(Sample).GetProperty(nameof(Sample.Email))!.GetCustomAttribute<HashedAuditAttribute>());
        Assert.NotNull(typeof(Sample).GetProperty(nameof(Sample.Token))!.GetCustomAttribute<RedactedAuditAttribute>());
    }
}

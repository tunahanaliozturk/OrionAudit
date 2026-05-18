using System.Reflection;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.Tests;

public class AttributesTests
{
    [Auditable]
    private sealed class Sample
    {
        public int Id { get; set; }
        [NotAuditable] public string Internal { get; set; } = "";
        [HashedAudit] public string Email { get; set; } = "";
        [RedactedAudit] public string Token { get; set; } = "";
    }

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

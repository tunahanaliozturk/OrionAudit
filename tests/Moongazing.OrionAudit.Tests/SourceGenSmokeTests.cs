using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

[OrionAuditModule]
public partial class SourceGenSmokeModule { }

[Auditable]
public sealed class SourceGenWidget
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class SourceGenSmokeTests
{
    [Fact]
    public void RegisterAuditedTypes_IsEmittedByGenerator_AndRegistersAuditableTypes()
    {
        var builder = new AuditConfigurationBuilder();
        SourceGenSmokeModule.RegisterAuditedTypes(builder);   // method emitted by source generator
        var config = builder.Build();

        Assert.True(config.IsAudited(typeof(SourceGenWidget)),
            "Generator-emitted RegisterAuditedTypes should register types decorated with [Auditable].");
    }

    [Fact]
    public void RegisterAuditedTypes_NullBuilder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SourceGenSmokeModule.RegisterAuditedTypes(null!));
    }
}

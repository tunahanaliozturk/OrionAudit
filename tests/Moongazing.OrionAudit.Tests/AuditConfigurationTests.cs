using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AuditConfigurationTests
{
    [Auditable]
    public sealed class AttrSample
    {
        public int Id { get; set; }
        [NotAuditable] public string Internal { get; set; } = "";
        [HashedAudit] public string Email { get; set; } = "";
    }

    public sealed class FluentSample
    {
        public int Id { get; set; }
        public string Internal { get; set; } = "";
        public string Email { get; set; } = "";
    }

    [Fact]
    public void Attribute_ConfiguredType_RegistersAllRules()
    {
        var builder = new AuditConfigurationBuilder();
        builder.Audit<AttrSample>();
        var config = builder.Build();

        Assert.True(config.IsAudited(typeof(AttrSample)));
        var typeConfig = config.GetConfig(typeof(AttrSample))!;
        Assert.Equal(AuditFieldRule.Exclude, typeConfig.FieldRule(nameof(AttrSample.Internal)));
        Assert.Equal(AuditFieldRule.Hash, typeConfig.FieldRule(nameof(AttrSample.Email)));
        Assert.Equal(AuditFieldRule.Capture, typeConfig.FieldRule(nameof(AttrSample.Id)));
    }

    [Fact]
    public void Fluent_OverridesProvideFieldRules()
    {
        var builder = new AuditConfigurationBuilder();
        builder.Audit<FluentSample>(b => b
            .Exclude(s => s.Internal)
            .Hash(s => s.Email));
        var config = builder.Build();

        Assert.True(config.IsAudited(typeof(FluentSample)));
        var typeConfig = config.GetConfig(typeof(FluentSample))!;
        Assert.Equal(AuditFieldRule.Exclude, typeConfig.FieldRule(nameof(FluentSample.Internal)));
        Assert.Equal(AuditFieldRule.Hash, typeConfig.FieldRule(nameof(FluentSample.Email)));
    }

    [Fact]
    public void IsAudited_ReturnsFalse_ForUnconfiguredType()
    {
        var builder = new AuditConfigurationBuilder();
        var config = builder.Build();
        Assert.False(config.IsAudited(typeof(string)));
    }

    [Fact]
    public void Fluent_OverridesAttribute_WhenBothPresent()
    {
        var builder = new AuditConfigurationBuilder();
        builder.Audit<AttrSample>(b => b.Redact(s => s.Email));   // attribute says Hash, fluent says Redact
        var config = builder.Build();

        var typeConfig = config.GetConfig(typeof(AttrSample))!;
        Assert.Equal(AuditFieldRule.Redact, typeConfig.FieldRule(nameof(AttrSample.Email)));
    }
}

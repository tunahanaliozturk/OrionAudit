namespace Moongazing.OrionAudit.Tests.Configuration;

using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;
using Xunit;

public sealed class PolymorphicCaptureTests
{
    private abstract class Document
    {
        public int Id { get; set; }
        public string Title { get; set; } = "";
    }

    private sealed class Invoice : Document
    {
        public decimal Amount { get; set; }
    }

    [Auditable(typeof(Document))]
    private sealed class Memo : Document
    {
        public string Body { get; set; } = "";
    }

    [Fact]
    public void AuditableAttribute_default_ctor_has_null_base_type()
    {
        var attr = new AuditableAttribute();
        Assert.Null(attr.BaseType);
    }

    [Fact]
    public void AuditableAttribute_typeof_ctor_records_base_type()
    {
        var attr = new AuditableAttribute(typeof(Document));
        Assert.Equal(typeof(Document), attr.BaseType);
    }

    [Fact]
    public void AuditableAttribute_typeof_ctor_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => new AuditableAttribute(null!));
    }

    [Fact]
    public void Builder_fluent_UseBaseType_attaches_to_config()
    {
        var config = new AuditConfigurationBuilder()
            .Audit<Invoice>(b => b.UseBaseType<Document>())
            .Build();

        var typeConfig = config.GetConfig(typeof(Invoice));
        Assert.NotNull(typeConfig);
        Assert.Equal(typeof(Document), typeConfig!.BaseType);
    }

    [Fact]
    public void Builder_attribute_path_picks_up_AuditableAttribute_base_type()
    {
        // Memo has [Auditable(typeof(Document))] at the class level.
        var config = new AuditConfigurationBuilder()
            .Audit(typeof(Memo))
            .Build();

        var typeConfig = config.GetConfig(typeof(Memo));
        Assert.NotNull(typeConfig);
        Assert.Equal(typeof(Document), typeConfig!.BaseType);
    }

    [Fact]
    public void Builder_no_base_type_leaves_config_BaseType_null()
    {
        var config = new AuditConfigurationBuilder()
            .Audit<Invoice>()
            .Build();

        var typeConfig = config.GetConfig(typeof(Invoice));
        Assert.NotNull(typeConfig);
        Assert.Null(typeConfig!.BaseType);
    }

    [Fact]
    public void AuditableTypeConfig_records_base_type()
    {
        var typeConfig = new AuditableTypeConfig(
            entityType: typeof(Invoice),
            rules: new Dictionary<string, AuditFieldRule>(),
            softDeleteProperty: null,
            baseType: typeof(Document));

        Assert.Equal(typeof(Document), typeConfig.BaseType);
        Assert.Equal(typeof(Invoice), typeConfig.EntityType);
    }

    [Fact]
    public void AuditLog_EntityBaseType_defaults_to_null()
    {
        var log = new AuditLog
        {
            EntityType = "Foo",
            EntityId = "1",
            Action = AuditAction.Inserted,
            OccurredOnUtc = DateTime.UtcNow,
        };
        Assert.Null(log.EntityBaseType);
    }

    [Fact]
    public void AuditLog_EntityBaseType_round_trips()
    {
        var log = new AuditLog
        {
            EntityType = "MyApp.Invoice",
            EntityBaseType = "MyApp.Document",
            EntityId = "1",
            Action = AuditAction.Updated,
            OccurredOnUtc = DateTime.UtcNow,
        };
        Assert.Equal("MyApp.Document", log.EntityBaseType);
    }
}

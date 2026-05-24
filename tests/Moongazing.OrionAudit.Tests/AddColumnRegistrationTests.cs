using Moongazing.OrionAudit;
using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class AddColumnRegistrationTests
{
    [Fact]
    public void AddColumn_RegistersWithNameClrTypeAndProvider()
    {
        var o = new OrionAuditOptions();
        o.AddColumn<int>("WorkflowStepId", _ => 7);
        var registered = Assert.Single(o.CustomColumns);
        Assert.Equal("WorkflowStepId", registered.Name);
        Assert.Equal(typeof(int), registered.ClrType);
    }

    [Fact]
    public void AddColumn_DuplicateName_Throws()
    {
        var o = new OrionAuditOptions();
        o.AddColumn<int>("X", _ => 1);
        Assert.Throws<OrionAuditConfigurationException>(() => o.AddColumn<string>("X", _ => "y"));
    }

    [Fact]
    public void AddColumn_UnsupportedType_Throws()
    {
        var o = new OrionAuditOptions();
        Assert.Throws<OrionAuditConfigurationException>(() => o.AddColumn<List<int>>("X", _ => null));
    }

    [Fact]
    public void AddColumn_NullOrEmptyName_Throws()
    {
        var o = new OrionAuditOptions();
        Assert.Throws<ArgumentException>(() => o.AddColumn<int>("", _ => 1));
        Assert.Throws<ArgumentException>(() => o.AddColumn<int>("   ", _ => 1));
        Assert.Throws<ArgumentNullException>(() => o.AddColumn<int>(null!, _ => 1));
    }

    [Fact]
    public void AddColumn_NullProvider_Throws()
    {
        var o = new OrionAuditOptions();
        Assert.Throws<ArgumentNullException>(() => o.AddColumn<int>("X", null!));
    }

    [Fact]
    public void AddColumn_ProviderBox_MatchesGenericReturn()
    {
        var o = new OrionAuditOptions();
        o.AddColumn<int>("X", _ => 42);
        var ctx = new AuditColumnContext(new object(), null!, AuditAction.Inserted, null, null);
        Assert.Equal(42, Assert.Single(o.CustomColumns).Provider(ctx));
    }
}

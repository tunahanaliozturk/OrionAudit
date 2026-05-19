using System.Reflection;
using Moongazing.OrionAudit;

namespace Moongazing.OrionAudit.Tests;

public class OrionAuditModuleAttributeTests
{
    [OrionAuditModule]
    public partial class TaggedModule { }

    [Fact]
    public void Attribute_IsDetectableViaReflection()
    {
        var attr = typeof(TaggedModule).GetCustomAttribute<OrionAuditModuleAttribute>();
        Assert.NotNull(attr);
    }

    [Fact]
    public void Attribute_IsClassOnly_AndNotInherited()
    {
        var usage = typeof(OrionAuditModuleAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;
        Assert.Equal(AttributeTargets.Class, usage.ValidOn);
        Assert.False(usage.AllowMultiple);
        Assert.False(usage.Inherited);
    }
}

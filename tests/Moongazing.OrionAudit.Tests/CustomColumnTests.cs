using Moongazing.OrionAudit.Configuration;

namespace Moongazing.OrionAudit.Tests;

public class CustomColumnTests
{
    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(double))]
    [InlineData(typeof(float))]
    [InlineData(typeof(short))]
    [InlineData(typeof(byte))]
    [InlineData(typeof(int?))]
    [InlineData(typeof(Guid?))]
    [InlineData(typeof(AuditAction))]   // enum
    [InlineData(typeof(AuditAction?))]  // nullable enum
    public void IsSupportedColumnType_Accepts_Scalars(Type t)
        => Assert.True(CustomColumn.IsSupportedColumnType(t));

    [Theory]
    [InlineData(typeof(object))]
    [InlineData(typeof(int[]))]
    [InlineData(typeof(List<string>))]
    [InlineData(typeof(CustomColumnTests))]
    public void IsSupportedColumnType_Rejects_NonScalars(Type t)
        => Assert.False(CustomColumn.IsSupportedColumnType(t));

    [Fact]
    public void Construct_HoldsName_ClrType_AndProvider()
    {
        var col = new CustomColumn("X", typeof(int), _ => 42);
        Assert.Equal("X", col.Name);
        Assert.Equal(typeof(int), col.ClrType);
        var ctx = new AuditColumnContext(new object(), null!, AuditAction.Inserted, null, null);
        Assert.Equal(42, col.Provider(ctx));
    }
}

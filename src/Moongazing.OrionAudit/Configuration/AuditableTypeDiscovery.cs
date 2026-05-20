using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Moongazing.OrionAudit.Configuration;

/// <summary>Scans assemblies for concrete classes decorated with <see cref="AuditableAttribute"/>.</summary>
public static class AuditableTypeDiscovery
{
    /// <summary>
    /// Returns all concrete public classes in the supplied assemblies that carry
    /// <see cref="AuditableAttribute"/>. Abstract classes and interfaces are skipped.
    /// </summary>
    /// <remarks>
    /// Uses runtime reflection (<c>Assembly.GetTypes()</c>) over every assembly, so trim and
    /// Native AOT publishes will flag every call site. For AOT consumers, declare an
    /// <c>[OrionAuditModule] partial class</c> and call its source-generated
    /// <c>RegisterAuditedTypes</c> instead — that path is reflection-free.
    /// </remarks>
    [RequiresUnreferencedCode("OrionAudit's assembly scan uses reflection over the supplied assemblies. Use the [OrionAuditModule] source generator and call its emitted RegisterAuditedTypes for trim-safe registration.")]
    [RequiresDynamicCode("OrionAudit's assembly scan uses reflection over the supplied assemblies.")]
    public static IReadOnlyList<Type> Discover(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var result = new List<Type>();
        foreach (var asm in assemblies)
        {
            foreach (var type in SafeGetTypes(asm))
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }
                if (type.GetCustomAttribute<AuditableAttribute>() is null)
                {
                    continue;
                }
                result.Add(type);
            }
        }
        return result;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).Cast<Type>();
        }
    }
}

using System.Reflection;

namespace Moongazing.OrionAudit.Configuration;

/// <summary>Scans assemblies for concrete classes decorated with <see cref="AuditableAttribute"/>.</summary>
public static class AuditableTypeDiscovery
{
    /// <summary>
    /// Returns all concrete public classes in the supplied assemblies that carry
    /// <see cref="AuditableAttribute"/>. Abstract classes and interfaces are skipped.
    /// </summary>
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

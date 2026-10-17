using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Jellyfin.Plugin.MediaVote.Web;

/// <summary>
/// Calls other plugins' static "PluginInterface" methods by reflection, so Media Vote has no hard
/// assembly dependency on them. Those methods take a Newtonsoft <c>JObject</c>; we build it by calling
/// <c>Parse(string)</c> on the parameter type the target method itself declares, so the object always
/// comes from the same Newtonsoft assembly the other plugin was loaded with (no load-context clash),
/// and Media Vote does not need to reference Newtonsoft.Json at all.
/// </summary>
public static class ReflectionBridge
{
    /// <summary>Finds a loaded assembly by simple name (e.g. "Jellyfin.Plugin.FileTransformation").</summary>
    public static Assembly? FindAssembly(string simpleName)
        => AssemblyLoadContext.All
            .SelectMany(c => c.Assemblies)
            .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.Ordinal));

    /// <summary>Invokes <c>{typeName}.{methodName}(JObject)</c> with <paramref name="payload"/> serialised to JSON.</summary>
    /// <returns>Null on success, otherwise a description of what went wrong.</returns>
    public static string? InvokeWithJObject(Assembly assembly, string typeName, string methodName, object payload)
    {
        var type = assembly.GetType(typeName);
        if (type is null)
        {
            return $"{typeName} not found in {assembly.GetName().Name} {assembly.GetName().Version}.";
        }

        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 1);
        if (method is null)
        {
            return $"{typeName}.{methodName} not found (unsupported version {assembly.GetName().Version}).";
        }

        var paramType = method.GetParameters()[0].ParameterType;
        var parse = paramType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
        if (parse is null)
        {
            return $"{typeName}.{methodName} takes {paramType.FullName}, which Media Vote cannot build.";
        }

        var json = JsonSerializer.Serialize(payload);
        var arg = parse.Invoke(null, new object[] { json });
        try
        {
            method.Invoke(null, new[] { arg });
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return $"{typeName}.{methodName} failed: {ex.InnerException.Message}";
        }

        return null;
    }
}

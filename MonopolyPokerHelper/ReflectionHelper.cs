using System;
using System.Reflection;
using static MonopolyPokerHelper.LoggerHelper;

namespace MonopolyPokerHelper;

internal static class ReflectionHelper
{
    // Common binding flags for reflection (shared across helpers)
    // Unsure if this is a good idea as it's possible some helpers might want to use different flags

    // TODO: Decide if we want to keep AllBindingFlags
    // It might be more flexible to not have a shared constant if different helpers have different needs, but it also might be nice to have a common set of flags for consistency. For now, I'll keep it as a constant for convenience, but we can always change it later if needed.
    public const BindingFlags AllBindingFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly string[] SingletonPropertyNames = ["Instance", "instance"];
    private static readonly string[] SingletonFieldNames = ["Instance", "instance", "_instance", "s_instance"];

    /// <summary>
    /// Searches all loaded assemblies for a type with the specified name and returns the corresponding Type object
    /// if found.
    /// </summary>
    /// <remarks>The search first attempts to find an exact match using the fully qualified name. If
    /// no match is found, it searches for a type with a matching simple name. Only assemblies currently loaded in
    /// the application domain are searched.</remarks>
    /// <param name="name">The fully qualified or simple name of the type to locate. This value is case-sensitive.</param>
    /// <returns>A Type object representing the type with the specified name, or null if the type cannot be found in any
    /// loaded assembly.</returns>
    public static Type FindType(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(name));

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        // Try exact match first
        foreach (var asm in assemblies)
        {
            var t = asm.GetType(name);
            if (t != null)
            {
                LogDebug(name + ": exact match found in " + asm.FullName);
                return t;
            }
        }

        // Try partial match (unqualified name)
        foreach (var asm in assemblies)
        {
            try
            {
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name == name || t.FullName == name)
                        return t;
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                LogDebug($"{name}: partial type load failure in {asm.FullName}: {ex.Message}");

                foreach (var t in ex.Types)
                {
                    if (t == null)
                        continue;

                    if (t.Name == name || t.FullName == name)
                    {
                        LogDebug(name + ": partial match found in " + asm.FullName);
                        return t;
                    }
                }
            }
            catch (NotSupportedException ex)
            {
                LogDebug($"{name}: unsupported reflection operation for {asm.FullName}: {ex.Message}");
            }
        }
        LogError(name + ": type not found in any loaded assembly!");
        return null;
    }

    /// <summary>
    /// Retrieves the singleton instance of the specified type, if available.
    /// </summary>
    /// <remarks>This method searches for a static property or field commonly named 'Instance',
    /// 'instance', '_instance', or 's_instance' on the specified type or its base types. If no such member is found
    /// or the value is null, the method returns null.</remarks>
    /// <param name="type">The type to search for a singleton instance.</param>
    /// <returns>The singleton instance of the specified type if found; otherwise, null.</returns>
    public static object GetSingletonInstance(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.Static;

        foreach (var propertyName in SingletonPropertyNames)
        {
            try
            {
                for (var current = type; current != null; current = current.BaseType)
                {
                    var prop = current.GetProperty(propertyName, all);
                    if (prop == null || prop.GetMethod == null || !prop.GetMethod.IsStatic)
                        continue;

                    var value = prop.GetValue(null);
                    if (value == null)
                        continue;

                    LogDebug($"Found singleton instance via property '{propertyName}' on type '{type.FullName}'");
                    return value;
                }
            }
            catch (AmbiguousMatchException ex)
            {
                LogDebug($"Skipping property '{propertyName}' on type '{type.FullName}' due to ambiguous match: {ex.Message}");
            }
            catch (TargetInvocationException ex)
            {
                LogDebug($"Skipping property '{propertyName}' on type '{type.FullName}' due to invocation error: {ex.Message}");
            }
            catch (MethodAccessException ex)
            {
                LogDebug($"Skipping property '{propertyName}' on type '{type.FullName}' due to access restriction: {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                LogDebug($"Skipping property '{propertyName}' on type '{type.FullName}' due to unsupported reflection operation: {ex.Message}");
            }
        }

        foreach (var fieldName in SingletonFieldNames)
        {
            try
            {
                for (var current = type; current != null; current = current.BaseType)
                {
                    var field = current.GetField(fieldName, all);
                    if (field == null || !field.IsStatic)
                        continue;

                    var value = field.GetValue(null);
                    if (value == null)
                        continue;

                    LogDebug($"Found singleton instance via field '{fieldName}' on type '{type.FullName}'");
                    return value;
                }
            }
            catch (AmbiguousMatchException ex)
            {
                LogDebug($"Skipping field '{fieldName}' on type '{type.FullName}' due to ambiguous match: {ex.Message}");
            }
            catch (FieldAccessException ex)
            {
                LogDebug($"Skipping field '{fieldName}' on type '{type.FullName}' due to access restriction: {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                LogDebug($"Skipping field '{fieldName}' on type '{type.FullName}' due to unsupported reflection operation: {ex.Message}");
            }
        }

        LogError($"Failed to find singleton instance for type '{type.FullName}'");
        return null;
    }
}
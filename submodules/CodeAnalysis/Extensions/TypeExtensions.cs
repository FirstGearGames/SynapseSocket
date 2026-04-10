using System;

namespace CodeAnalysis.Extensions;

public static class TypeExtensions
{

    /// <summary>
    /// Gets the full name of a Type while removing generic arguments and brackets.
    /// </summary>
    /// <remarks>The returned string does not include the global alias.</remarks>
    public static string? GetFullNameWithoutGenerics(this Type type)
    {
        string? fullName = type.FullName;
        
        if (fullName is null)
            return null;
        
        int genericMarkerIndex = fullName.IndexOf("`", StringComparison.InvariantCultureIgnoreCase);
        if (genericMarkerIndex >= 0)
            return fullName.Substring(0, genericMarkerIndex);
            
        return fullName;
    }
        
        
    /// <summary>
    /// Gets the name of a Type while removing generic arguments and brackets.
    /// </summary>
    /// <remarks>The returned string does not include the global alias.</remarks>
    public static string GetNameWithoutGenerics(this Type type)
    {
        string name = type.Name;

        int genericMarkerIndex = name.IndexOf("`", StringComparison.InvariantCultureIgnoreCase);
        if (genericMarkerIndex >= 0)
            return name.Substring(0, genericMarkerIndex);
            
        return name;
    }
}
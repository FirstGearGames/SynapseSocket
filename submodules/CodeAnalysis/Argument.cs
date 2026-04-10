using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace CodeAnalysis;

public readonly struct Argument
{
    public readonly ITypeSymbol TypeSymbol;
    public readonly string Name;
    public readonly bool IsNamed;

    public Argument(ITypeSymbol typeSymbol, string name, bool isNamed)
    {
        TypeSymbol = typeSymbol;
        Name = name;
        IsNamed = isNamed;
    }
}

public static class ArgumentExtensions 
{
    /// <summary>
    /// Returns if provided arguments are named. 
    /// </summary>
    /// <returns>True if there are no arguments present, or if all arguments are named.</returns>
    public static bool AreArgumentsEmptyOrNamed(this List<Argument> methodArguments)
    {
        if (methodArguments is null || methodArguments.Count == 0)
            return true;

        foreach (Argument argument in methodArguments)
        {
            if (!argument.IsNamed)
                return false;
        }

        return true;
    }
}
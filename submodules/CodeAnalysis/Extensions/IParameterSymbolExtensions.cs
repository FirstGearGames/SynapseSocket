using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace CodeAnalysis.Extensions;

public static class ParameterSymbolExtensions
{
    /// <summary>
    /// Returns ParameterSymbols as a MethodParameter collection.
    /// </summary>
    /// <returns></returns>
    public static List<MethodParameter> GetMethodParameters(this IEnumerable<IParameterSymbol> parameterSymbols)
    {
        List<MethodParameter> methodParameters = new();

        foreach (IParameterSymbol parameterSymbol in parameterSymbols)
            methodParameters.Add(new(parameterSymbol));

        return methodParameters;
    }
        
    public static string OptionalValueToString(this IParameterSymbol thisValue)
    {
        if (!thisValue.HasExplicitDefaultValue)
            return string.Empty;

        object? v = thisValue.ExplicitDefaultValue;

        if (v is null)
            return string.Empty;

        if (thisValue.Type.TypeKind == TypeKind.Enum)
        {
            INamedTypeSymbol enumType = (INamedTypeSymbol)thisValue.Type;
            IFieldSymbol? match = enumType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, v));

            if (match is not null)
                return $"{enumType.Name}.{match.Name}";
        }

        return v switch
        {
            string s => s,
            char c => $"'{c}'",
            bool b => b ? "true" : "false",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? v.ToString() ?? "Unprintable"
        };
    }

    public static bool TypeFullNameEquals(this IParameterSymbol parameterSymbol, IParameterSymbol otherParameterSymbol) => parameterSymbol.TypeFullNameEquals(otherParameterSymbol.Type.GetTypeSymbolFullName());
    public static bool TypeFullNameEquals(this IParameterSymbol parameterSymbol, string? otherTypeFullName) => parameterSymbol.Type.GetTypeSymbolFullName().Equals(otherTypeFullName);
}
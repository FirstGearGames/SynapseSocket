using System.Collections.Generic;
using System.Text;
using CodeAnalysis.Extensions;
using CodeAnalysis.Finding;
using Microsoft.CodeAnalysis;

namespace CodeAnalysis;

public readonly struct MethodParameter
{
    public readonly string TypeFullName;
    public readonly string ParameterName;
    public readonly string OptionalValue;
    public readonly int Index;
    public MethodParameter(IParameterSymbol parameterSymbol) : this(parameterSymbol.Type.GetTypeSymbolFullNameWithArguments(ArgumentSearchType.PreferNamed, out _), parameterSymbol.Name, parameterSymbol.Ordinal, parameterSymbol.OptionalValueToString()) { }
        
    public MethodParameter(string? typeFullName, string parameterName,int index, string optionalValue)
    {
        if (typeFullName is null)
            typeFullName = string.Empty;
            
        TypeFullName = typeFullName;
        ParameterName = parameterName;
        Index = index;
        OptionalValue = optionalValue;
    }

    /// <summary>
    /// Returns the parameter type and name as it would be seen in a method signature.
    /// </summary>
    /// <returns></returns>
    public string GetParameterAsMethodSignature() => $"{TypeFullName} {ParameterName}";
        
}

public static class MethodParameterExtensions
{
    /// <summary>
    /// Returns all entries as they would appear in a method signature.
    /// </summary>
    /// <example>bool isSafe, int healthRemaining</example>
    public static string GetAsMethodSignature(this List<MethodParameter> thisValue)
    {
        if (thisValue is null || thisValue.Count == 0)
            return string.Empty;

        StringBuilder stringBuilder = new();
        List<string> parametersAsSignatures = [];

        foreach (MethodParameter methodParameter in thisValue)
        {
            stringBuilder.Clear();

            stringBuilder.Append($"{methodParameter.TypeFullName} {methodParameter.ParameterName}");

            if (!string.IsNullOrWhiteSpace(methodParameter.OptionalValue))
            {
                stringBuilder.Append($" = {methodParameter.OptionalValue}");
                    
                /* If the type is a Single then the f to indicate
                 * float has to manually be added for compilation to complete. */
                bool typeIsSingle = methodParameter.TypeFullName.Equals(typeof(System.Single).FullName);
                if (typeIsSingle)
                    stringBuilder.Append("f");
            }

            parametersAsSignatures.Add(stringBuilder.ToString());
        }

        return string.Join(", ", parametersAsSignatures);
    }

    /// <summary>
    /// Returns the name only of each parameter.
    /// </summary>
    public static List<string> GetParameterNames(this List<MethodParameter> methodParameters)
    {
        if (methodParameters is null || methodParameters.Count == 0)
            return [];

        List<string> names = [];

        for (int i = 0; i < methodParameters.Count; i++)
            names.Add(methodParameters[i].ParameterName);

        return names;
    }

    /// <summary>
    /// Returns the type full name of each parameter.
    /// </summary>
    public static List<string> GetParameterTypeFullNames(this List<MethodParameter> methodParameters)
    {
        if (methodParameters is null || methodParameters.Count == 0)
            return [];

        List<string> names = [];

        for (int i = 0; i < methodParameters.Count; i++)
            names.Add(methodParameters[i].TypeFullName);

        return names;
    }
}
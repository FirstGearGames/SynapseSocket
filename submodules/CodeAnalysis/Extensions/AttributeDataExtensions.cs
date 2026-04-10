#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAnalysis.Extensions;

public static class AttributeDataExtensions
{
    // public static IReadOnlyList<AttributeData> GetAttributes(this SyntaxList<AttributeListSyntax> syntaxList, Compilation compilation)
    // {
    //     return null;
    //     // List<AttributeData> attributes = new();
    //     //
    //     // foreach (AttributeListSyntax atrList in syntaxList)
    //     // {
    //     //     if (atrList is null)
    //     //         continue;
    //     //     
    //     //     attributes.AddRange(atrList.GetAttributes(compilation));
    //     // }
    //     //
    //     // return attributes;
    // }
        
    public static IReadOnlyList<AttributeData> GetAttributes(this AttributeListSyntax attributes, Compilation compilation)
    {
        // Collect pertinent syntax trees from these attributes
        HashSet<SyntaxTree> acceptedTrees = new();
        foreach (AttributeSyntax attribute in attributes.Attributes)
            acceptedTrees.Add(attribute.SyntaxTree);

        List<AttributeData> ret = new();

        ISymbol parentSymbol = attributes.Parent?.GetDeclaredSymbol(compilation);
        if (parentSymbol is not null)
        {
            ImmutableArray<AttributeData> parentAttributes = parentSymbol.GetAttributes();
            foreach (AttributeData attribute in parentAttributes)
            {
                if (acceptedTrees.Contains(attribute.ApplicationSyntaxReference!.SyntaxTree))
                    ret.Add(attribute);
            }
        }

        return ret;
    }

    public static bool HasAttribute(this SyntaxList<AttributeListSyntax> syntaxList, string attributeFullName)
    {
        foreach (AttributeListSyntax atrList in syntaxList)
        {
            if (atrList.HasAttribute(attributeFullName))
                return true;
        }

        return false;
    }

    public static bool HasAttribute(this AttributeListSyntax attributeListSyntax,string attributeFullName)
    {
        if (attributeListSyntax == null)
            return false;
            
        foreach (AttributeSyntax atr in attributeListSyntax.Attributes)
        {
            if (atr.Name.ToString() == attributeFullName)
                return true;
        }
            
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T0? GetConstructorArgument<T0>(this AttributeData thisAttributeData, int argumentIndex)
    {
        ImmutableArray<TypedConstant> constructorArguments = thisAttributeData.ConstructorArguments;

        if (argumentIndex > -1 && argumentIndex < constructorArguments.Length)
            return (T0)constructorArguments[argumentIndex].Value;

        return default;
    }


    public static T0? GetNamedArgument<T0>(this AttributeData thisAttributeData, int argumentIndex)
    {
        ImmutableArray<KeyValuePair<string, TypedConstant>> namedArguments = thisAttributeData.NamedArguments;

        if (argumentIndex > -1 && argumentIndex < namedArguments.Length)
            return (T0)namedArguments[argumentIndex].Value.Value;

        return default;
    }

    public static T0? GetNamedArgument<T0>(this AttributeData thisAttributeData, string argumentName) => thisAttributeData.GetNamedArgument<T0>(argumentName, default);

    public static T0? GetNamedArgument<T0>(this AttributeData thisAttributeData, string argumentName, T0? defaultValue)
    {
        foreach (KeyValuePair<string, TypedConstant> namedArgument in thisAttributeData.NamedArguments)
            if (namedArgument.Key == argumentName)
                return (T0)namedArgument.Value.Value;

        return defaultValue;
    }
}
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
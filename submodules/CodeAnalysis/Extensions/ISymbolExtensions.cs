using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAnalysis.Extensions;

public static class SymbolExtensions
{
    /// <summary>
    /// Gets the specific location of the identifier (the name) of a method or class.
    /// This makes diagnostics look much cleaner in the IDE.
    /// </summary>
    public static Location GetIdentifierLocation(this ISymbol symbol)
    {
        if (!symbol.TryGetRecordDeclaration(out SyntaxNode? syntaxNode))
            return Location.None;

        // Use pattern matching or simple casting for older Roslyn
        if (syntaxNode is MethodDeclarationSyntax method)
            return method.Identifier.GetLocation();

        if (syntaxNode is ClassDeclarationSyntax cls)
            return cls.Identifier.GetLocation();

        return syntaxNode!.GetLocation();
    }

    /// <summary>
    /// Gets the SyntaxNode for a symbol if it exists in source.
    /// </summary>
    public static bool TryGetRecordDeclaration(this ISymbol symbol, out SyntaxNode? syntaxNode)
    {
        SyntaxReference? reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        syntaxNode = reference?.GetSyntax();

        return syntaxNode is not null;
    }

    /// <summary>
    /// Returns the full name of a symbol which includes the namespace.
    /// </summary>
    public static string GetNamespace(this ISymbol symbol)
    {
        return symbol?.ContainingNamespace?.Name ?? string.Empty;
    }

    /// <summary>
    /// Returns the full name of a symbol which includes the namespace.
    /// </summary>
    public static string GetSymbolFullName(this ISymbol symbol)
    {
        if (symbol is null)
            return string.Empty;

        if (symbol is ITypeSymbol typeSymbol)
            return typeSymbol.GetTypeSymbolFullName();

        string containingNamespace = symbol.ContainingNamespace?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty;
        containingNamespace = containingNamespace.RemoveGlobalAlias();

        string fullyQualifiedName = string.Empty;
        string joiningChar = Finding.Constants.NamespaceNameJoiningCharacter;
        for (INamedTypeSymbol currentType = symbol.ContainingType; currentType is not null; currentType = currentType.ContainingType)
            fullyQualifiedName = $"{currentType.Name}{joiningChar}{fullyQualifiedName}";

        fullyQualifiedName = $"{fullyQualifiedName}{symbol.Name}";

        return string.IsNullOrWhiteSpace(containingNamespace) ? fullyQualifiedName : $"{containingNamespace}.{fullyQualifiedName}";
    }

    /// <summary>
    /// Returns if a symbol has any of the provided attribute.
    /// </summary>
    public static bool HasAnyAttribute(this ISymbol symbol, SearchScope searchScope, List<Type> attributeTypes, out List<AttributeData> data)
    {
        if (attributeTypes is null)
        {
            data = null;
            return false;
        }
        List<string> fullNames = [];
        foreach (Type type in attributeTypes)
        {
            string? typeFullName = type?.FullName;
            if (typeFullName is not null)
                fullNames.Add(typeFullName);
        }

        return symbol.HasAnyAttribute(searchScope, fullNames, out data);
    }

    /// <summary>
    /// Returns if a symbol has an attribute.
    /// </summary>
    public static bool HasAttribute(this ISymbol symbol, SearchScope searchScope, Type attributeType, out AttributeData data)
    {
        if (attributeType is null)
        {
            data = null;
            return false;
        }

        string? typeFullName = attributeType.FullName;
            
        return symbol.HasAttribute(searchScope, typeFullName, out data);
    }

    /// <summary>
    /// Returns if a symbol has an attribute, and outputs it if so.
    /// </summary>
    public static bool HasAttribute(this ISymbol symbol, SearchScope searchScope, string? attributeFullName, out AttributeData data)
    {
        if (attributeFullName is null)
        {
            data = null;
            return false;
        }

        if (symbol is not null)
        {
            foreach (AttributeData item in symbol.GetAttributes())
            {
                INamedTypeSymbol? typeSymbol = item.AttributeClass;

                while (typeSymbol is not null)
                {
                    if (typeSymbol.GetSymbolFullName() == attributeFullName)
                    {
                        data = item;
                        return true;
                    }

                    typeSymbol = searchScope == SearchScope.Hierarchy ? typeSymbol.BaseType : null;
                }
            }
        }

        data = null;

        return false;
    }

    /// <summary>
    /// Returns if a symbol has any of the supplied attributes, and outputs it if so.
    /// </summary>
    public static bool HasAnyAttribute(this ISymbol symbol, SearchScope searchScope, List<string> attributeFullNames, out List<AttributeData> data)
    {
        data = new();

        if (symbol is null)
            return false;

        foreach (string fullName in attributeFullNames)
        {
            if (symbol.HasAttribute(searchScope, fullName, out AttributeData lData))
                data.Add(lData);
        }

        return data.Count > 0;
    }

    /// <summary>
    /// True if symbol inherits base class anywhere along hierarchy.
    /// </summary>
    public static bool InheritsClass(this ISymbol symbol, string classFullName)
    {
        if (symbol is INamedTypeSymbol namedTypeSymbol)
            return namedTypeSymbol.InheritsClass(classFullName);

        return false;
    }

    /// <summary>
    /// Gets the INamedTypeSymbol for the Type member of an IFieldSymbol or IPropertySymbol. 
    /// </summary>
    public static bool TryGetMemberTypeINamedTypeSymbol(this ISymbol symbol, out INamedTypeSymbol? namedTypeSymbol)
    {
        namedTypeSymbol = null;

        if (symbol is IFieldSymbol fieldSymbol)
        {
            if (fieldSymbol.Type is not INamedTypeSymbol lNamedTypeSymbol)
                return false;

            namedTypeSymbol = lNamedTypeSymbol;
        }
        else if (symbol is IPropertySymbol propertySymbol)
        {
            if (propertySymbol.Type is not INamedTypeSymbol lNamedTypeSymbol)
                return false;

            namedTypeSymbol = lNamedTypeSymbol;
        }

        return namedTypeSymbol is not null;
    }
    

}
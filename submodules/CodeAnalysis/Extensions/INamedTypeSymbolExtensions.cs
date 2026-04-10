#pragma warning disable CS8602 // Dereference of a possibly null reference.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAnalysis.Extensions;

public static class NamedTypeSymbolExtensions
{
    /// <summary>
    /// Sets the namespace and class signature.
    /// </summary>
    /// <returns>True if information was set successfully.</returns>
    /// <example>Generated class signature example: public partial MyClass<Type> : BaseClass, Interface</example>
    public static bool TryGenerateClassSignature(this INamedTypeSymbol classSymbol, out string? fullNamespace, out string? classDeclaration)
    {
        fullNamespace = null;
        classDeclaration = null;

        if (!classSymbol.IsReferenceType)
            return false;

        fullNamespace = classSymbol.GetNamespace();
            
        StringBuilder stringBuilder = new();
            
        /* Get modifiers. */
        SyntaxReference? classSyntaxReference = classSymbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (classSyntaxReference is null)
            return false;
            
        TypeDeclarationSyntax typeDeclarationSyntax = (TypeDeclarationSyntax)classSyntaxReference.GetSyntax();
        string generatedModifiers = string.Join(" ", typeDeclarationSyntax.Modifiers.Select(x => x.Text));
            
        // public partial 
        stringBuilder.Append($"{generatedModifiers} class ");
            
        // Formatting for ToDisplayString.
        SymbolDisplayFormat thisStringFormat = new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly, genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters, kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword, memberOptions: SymbolDisplayMemberOptions.IncludeModifiers, miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
        // public partial class Name<Parameters>
        stringBuilder.Append(classSymbol.ToDisplayString(thisStringFormat));

        bool hasBaseClass = classSymbol.BaseType is not null && classSymbol.BaseType.SpecialType is not SpecialType.System_Object;

        SymbolDisplayFormat fullNameWithParameterFormat = new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces, genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters, kindOptions: SymbolDisplayKindOptions.None, memberOptions: SymbolDisplayMemberOptions.IncludeModifiers, miscellaneousOptions: SymbolDisplayMiscellaneousOptions.None);

        // public partial class Name<Parameters> : Namespace.BaseClass<Parameters>
        if (hasBaseClass)
            stringBuilder.Append($" : {classSymbol.BaseType.ToDisplayString(fullNameWithParameterFormat)}");

        for (int i = 0; i < classSymbol.Interfaces.Length; i++)
        {
            INamedTypeSymbol interfaceSymbol = classSymbol.Interfaces[i];

            /* If there is a base class or if this is the second interface
             * prefix with a comma, otherwise use a colon. */
            string prefix = hasBaseClass || i > 0 ? ", " : " : ";
            stringBuilder.Append(prefix);
                
            stringBuilder.Append(interfaceSymbol.ToDisplayString(fullNameWithParameterFormat));
        }
            
        classDeclaration = stringBuilder.ToString();
            
        return true;
    }

    /// <summary>
    /// Gets IFieldSymbols within a symbol.
    /// </summary>
    public static List<IFieldSymbol> GetFieldSymbols(this INamedTypeSymbol namedTypeSymbol, Accessibility? requiredAccessibility)
    {
        List<IFieldSymbol> validSymbols = new();

        foreach (ISymbol symbol in namedTypeSymbol.GetMembers())
        {
            if (symbol is IFieldSymbol methodSymbol)
            {
                if (requiredAccessibility is null || methodSymbol.DeclaredAccessibility == requiredAccessibility.Value)
                    validSymbols.Add(methodSymbol);
            }
        }

        return validSymbols;
    }

    /// <summary>
    /// Gets IMethodSymbols within an INamedTypeSymbols.
    /// </summary>
    public static List<IMethodSymbol> GetMethodSymbols(this INamedTypeSymbol namedTypeSymbol, Accessibility? requiredAccessibility)
    {
        List<IMethodSymbol> validSymbols = new();

        foreach (ISymbol symbol in namedTypeSymbol.GetMembers())
        {
            if (symbol is IMethodSymbol methodSymbol)
            {
                if (requiredAccessibility is null || methodSymbol.DeclaredAccessibility == requiredAccessibility.Value)
                    validSymbols.Add(methodSymbol);
            }
        }

        return validSymbols;
    }

    /// <summary>
    /// Returns the short name of a symbol which includes the namespace.
    /// </summary>
    public static bool NamedTypeSymbolImplementsInterface(this INamedTypeSymbol symbol, string? interfaceFullName)
    {
        if (interfaceFullName is null)
            return false;

        foreach (INamedTypeSymbol interfaceNamed in symbol.Interfaces)
        {
            if (interfaceNamed.GetTypeSymbolFullName() == interfaceFullName)
                return true;
        }

        return false;
    }

    /// <summary>
    /// True if symbol inherits base class anywhere along hierarchy.
    /// </summary>
    public static bool InheritsClass(this INamedTypeSymbol symbol, string classFullName)
    {
        while (symbol.BaseType is { } baseSymbol)
        {
            if (baseSymbol.GetTypeSymbolFullName() == classFullName)
                return true;

            symbol = symbol.BaseType;
        }

        return false;
    }

    /// <summary>
    /// Returns a method containing matching parameter names.
    /// </summary>
    public static IMethodSymbol? GetMethod(this INamedTypeSymbol symbol, string methodName, params string[] parameterNames)
    {
        IEnumerable<IMethodSymbol> methodSymbols = symbol.GetMembers(methodName).OfType<IMethodSymbol>();

        foreach (IMethodSymbol methodSymbol in methodSymbols)
        {
            if (methodSymbol.AreParametersMatching(parameterNames))
                return methodSymbol;
        }

        return null;
    }

    /// <summary>
    /// Returns if a type has public accessibility.
    /// </summary>
    public static bool HasPublicAccessibility(this INamedTypeSymbol namedTypeSymbol) => namedTypeSymbol.DeclaredAccessibility is Accessibility.Public;

    /// <summary>
    /// Returns if a type has public accessibility.
    /// </summary>
    public static bool HasPartialModifier(this INamedTypeSymbol namedTypeSymbol)
    {
        if (namedTypeSymbol is null)
            return false;

        ImmutableArray<SyntaxReference> syntaxReferences = namedTypeSymbol.DeclaringSyntaxReferences;

        // If there's more than one reference then we know it's partial
        if (syntaxReferences.Length > 1)
            return true;

        SyntaxReference? firstSyntaxReference = syntaxReferences.FirstOrDefault();
        if (firstSyntaxReference is null)
            return false;

        if (firstSyntaxReference.GetSyntax() is ClassDeclarationSyntax classDeclarationSyntax)
            return classDeclarationSyntax.Modifiers.Any(SyntaxKind.PartialKeyword);
        if (firstSyntaxReference.GetSyntax() is StructDeclarationSyntax structDeclarationSyntax)
            return structDeclarationSyntax.Modifiers.Any(SyntaxKind.PartialKeyword);

        return false;
    }

    /// <summary>
    /// Returns a types header as a string. (Eg: public partial class MyClass).
    /// </summary>
    /// <param name = "classSymbol"></param>
    /// <returns></returns>
    public static string GetClassOrStructHeader(this INamedTypeSymbol namedTypeSymbol)
    {
        string keywordText;
        if (!IsAllowedKeyword(out keywordText))
            return string.Empty;

        // Public, internal, etc. 
        string modifiersText = namedTypeSymbol.DeclaredAccessibility.ToString().ToLower();
        // Partial check.
        string partialText = HasPartialModifier(namedTypeSymbol) ? "partial " : string.Empty;

        bool IsAllowedKeyword(out string lKeyword)
        {
            lKeyword = string.Empty;

            if (namedTypeSymbol.TypeKind == TypeKind.Class)
                lKeyword = "class";
            else if (namedTypeSymbol.TypeKind == TypeKind.Struct)
                lKeyword = "struct";
            else
                return false;

            return true;
        }

        return $"{modifiersText} {partialText}{keywordText} {namedTypeSymbol.Name}";
    }
    
    /// <summary>
    /// Returns if the symbol implements IEquatable.
    /// </summary>
    /// <returns>True if implemented.</returns>
    public static bool ImplementsIEquatable(this INamedTypeSymbol namedTypeSymbol)
    {
        string? iEquatableFullName = typeof(IEquatable<>).FullName;
        if (iEquatableFullName is null)
            return false;
        
        return namedTypeSymbol.NamedTypeSymbolImplementsInterface(iEquatableFullName);
    }
    
    /// <summary>
    /// Returns if the symbol implements operator==.
    /// </summary>
    /// <returns>True if implemented.</returns>
    public static bool ImplementsOpEquality(this INamedTypeSymbol namedTypeSymbol)
    {
        foreach (ISymbol member in namedTypeSymbol.GetMembers(WellKnownMemberNames.EqualityOperatorName))
        {
            // Cast to IMethodSymbol to check the MethodKind
            if (member is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } method)
            {
                //Two parameters are expected for the operator== method.
                if (method.Parameters.Length == 2)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns if the NamedTypeSymbol can be compared using == and != operators.
    /// </summary>
    public static bool IsEqualityComparable(this INamedTypeSymbol namedTypeSymbol) => namedTypeSymbol.IsValueType && !namedTypeSymbol.IsClassOrStruct();

}
#pragma warning restore CS8602 // Dereference of a possibly null reference.
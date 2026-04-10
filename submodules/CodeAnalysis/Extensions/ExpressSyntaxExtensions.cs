using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAnalysis.Extensions;

public static class ExpressSyntaxExtensions
{
    /// <summary>
    /// Returns ITypeSymbol for a TypeOfExpressionSyntax.
    /// </summary>
    public static ITypeSymbol? GetTypeIdentifier(this TypeOfExpressionSyntax syntax, SemanticModel semanticModel) => semanticModel?.GetSymbolInfo(syntax.Type).Symbol as ITypeSymbol;

    /// <summary>
    /// Returns ITypeSymbol for an expression syntax using GetTypeInfo.
    /// </summary>
    public static ITypeSymbol? GetTypeInfoTypeSymbol(this ExpressionSyntax syntax, SemanticModel semanticModel) => semanticModel?.GetTypeInfo(syntax).Type;
}
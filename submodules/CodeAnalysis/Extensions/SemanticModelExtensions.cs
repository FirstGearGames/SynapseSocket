#pragma warning disable CS8603 // Possible null reference return.
#nullable enable
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeAnalysis.Extensions;

public static class SemanticModelExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISymbol GetSymbol(this SemanticModel semanticModel, SyntaxNode node)
    {
        return semanticModel.GetSymbolInfo(node).Symbol;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ITypeSymbol GetTypeSymbol(this SemanticModel semanticModel, SyntaxNode node)
    {
        return semanticModel.GetTypeInfo(node).Type;
    }

    /// <summary>
    /// Returns IFieldSymbol from a FieldDelarationSynthax.
    /// </summary>
    public static IFieldSymbol GetFieldSymbol(this SemanticModel semanticModel, FieldDeclarationSyntax fieldDeclaration)
    {
        if (fieldDeclaration.Declaration.Variables.Count == 0)
            return null;

        VariableDeclaratorSyntax variableDeclaratorSyntax = fieldDeclaration.Declaration.Variables[0];
        ISymbol? symbol = ModelExtensions.GetDeclaredSymbol(semanticModel, variableDeclaratorSyntax);

        if (symbol is IFieldSymbol fieldSymbol)
            return fieldSymbol;

        return null;
    }
        
}
#pragma warning restore CS8603 // Possible null reference return.
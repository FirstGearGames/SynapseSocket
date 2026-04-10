
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.

#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
using Microsoft.CodeAnalysis;

namespace CodeAnalysis.Extensions;

public static class SyntaxNodeExtensions
{
    public static ISymbol? GetDeclaredSymbol(this SyntaxNode node, Compilation compilation)
    {
        SemanticModel model = compilation.GetSemanticModel(node.SyntaxTree);
        return model?.GetDeclaredSymbol(node);
    }

    public static bool TryGetParentSyntax<T0>(this SyntaxNode syntaxNode, out T0? result) where T0 : SyntaxNode
    {
        // set defaults
        #pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
        result = null;
        #pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.

        if (syntaxNode is null)
        {
            return false;
        }

        try
        {
            syntaxNode = syntaxNode.Parent;

            if (syntaxNode is null)
            {
                return false;
            }

            if (syntaxNode.GetType() == typeof(T0))
            {
                result = syntaxNode as T0;
                return true;
            }

            return TryGetParentSyntax(syntaxNode, out result);
        }
        catch
        {
            return false;
        }
    }
}
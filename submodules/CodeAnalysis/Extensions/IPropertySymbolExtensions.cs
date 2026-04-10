using Microsoft.CodeAnalysis;

namespace CodeAnalysis.Extensions;

public static class PropertySymbolExtensions
{
    /// <summary>
    /// Gets if both accessors exist and are public for symbol are public.
    /// </summary>
    public static bool AreAccessorsPublic(this IPropertySymbol symbol)
    {
        bool isGetterPublic = false;
        bool isSetterPublic = false;

        if (symbol.GetMethod != null)
            isGetterPublic = symbol.GetMethod.DeclaredAccessibility == Accessibility.Public;

        if (symbol.SetMethod != null)
            isSetterPublic = symbol.SetMethod.DeclaredAccessibility == Accessibility.Public;

        return isGetterPublic && isSetterPublic;
    }
}
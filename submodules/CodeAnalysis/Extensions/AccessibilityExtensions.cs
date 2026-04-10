
using Microsoft.CodeAnalysis;

namespace CodeAnalysis.Extensions;

public static class AccessibilityExtensions
{
    /// <summary>
    /// Generates code for an accessibility.
    /// </summary>
    public static string GenerateAccessibility(this Accessibility accessibility)
    {
        return accessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.Protected => "protected",
            Accessibility.Private => "private",
            Accessibility.ProtectedAndInternal => "protected internal",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.NotApplicable => "private",
            _ => "public"
        };
    }

}
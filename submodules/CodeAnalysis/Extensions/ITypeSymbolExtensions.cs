using System.Collections.Generic;
using System.Text;
using CodeAnalysis.Constants;
using CodeAnalysis.Finding;
using Microsoft.CodeAnalysis;

namespace CodeAnalysis.Extensions;

public static class TypeSymbolExtensions
{
    /// <summary>
    /// Gets the full name of a TypeSymbol.
    /// </summary>
    public static string GetTypeSymbolFullName(this ITypeSymbol typeSymbol)
    {
        if (typeSymbol is null)
            return string.Empty;

        if (typeSymbol is IArrayTypeSymbol arrayTypeSymbol)
            typeSymbol = arrayTypeSymbol.ElementType;

        // If generic then just return our const for generic.
        if (typeSymbol.TypeKind is TypeKind.TypeParameter)
            return NativeConstants.FirstGenericParameterName;

        string containingNamespace = typeSymbol.ContainingNamespace?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty;
        containingNamespace = containingNamespace.RemoveGlobalAlias();

        string fullyQualifiedName = string.Empty;
        string joiningChar = Finding.Constants.NamespaceNameJoiningCharacter;
        for (INamedTypeSymbol currentType = typeSymbol.ContainingType; currentType is not null; currentType = currentType.ContainingType)
            fullyQualifiedName = $"{currentType.Name}{joiningChar}{fullyQualifiedName}";

        // fullyQualifiedName = $"{fullyQualifiedName}{typeSymbol.Name}{arraySuffix}";
        fullyQualifiedName = $"{fullyQualifiedName}{typeSymbol.Name}";

        return string.IsNullOrWhiteSpace(containingNamespace) ? fullyQualifiedName : $"{containingNamespace}{joiningChar}{fullyQualifiedName}";
    }

    /// <summary>
    /// Returns full name with generic arguments as named types (System.Collections.Generic.List<System.String>).
    /// </summary>
    public static string GetTypeSymbolFullNameWithArguments(this ITypeSymbol typeSymbol, ArgumentSearchType argumentSearchType, out ArgumentSearchResult argumentSearchResult)
    {
        /* If is an array then just use the extension method
         * to return the type named as an array. */
        if (typeSymbol is IArrayTypeSymbol arrayTypeSymbol)
            return arrayTypeSymbol.GetArrayTypeSymbolFullNameWithArguments(argumentSearchType, out argumentSearchResult);

        if (typeSymbol is INamedTypeSymbol namedTypeSymbol)
            return namedTypeSymbol.GetNamedTypeSymbolFullNameWithArguments(argumentSearchType, out argumentSearchResult);

        argumentSearchResult = ArgumentSearchResult.ErrorForSearchType;
        return string.Empty;
    }

    /// <summary>
    /// Returns type as a generic array, if an array (T0[], T0[][]). If not an array empty is returned.
    /// </summary>
    public static string GetArrayTypeSymbolFullNameWithArguments(this IArrayTypeSymbol arrayTypeSymbol, ArgumentSearchType argumentSearchType, out ArgumentSearchResult argumentSearchResult)
    {
        StringBuilder stringBuilder = new();

        bool isSearchTypeExplicitlyNamed = argumentSearchType is ArgumentSearchType.ExplicitlyNamed;

        //Expecting named arguments, but they are not named.
        if (isSearchTypeExplicitlyNamed && !arrayTypeSymbol.ArePresentArgumentsNamed())
        {
            argumentSearchResult = ArgumentSearchResult.ErrorForSearchType;
            return string.Empty;
        }

        string symbolName = argumentSearchType == ArgumentSearchType.Generic ? NativeConstants.FirstGenericParameterName : arrayTypeSymbol.ElementType.GetTypeSymbolFullName();
        stringBuilder.Append($"{symbolName}[");

        //Add any additional sub-arrays.
        TryAppendMultidimensionalAndJagged(arrayTypeSymbol);

        /* Tries to append a multidimensional or jagged array.
         * True is returned if succesful or no append is required,
         * false is returned on error. */
        bool TryAppendMultidimensionalAndJagged(IArrayTypeSymbol arrSym)
        {
            for (int i = 1; i < arrSym.Rank; i++)
                stringBuilder.Append(",");

            if (arrSym.ElementType is IArrayTypeSymbol jaggedElement)
            {
                if (isSearchTypeExplicitlyNamed && !jaggedElement.ArePresentArgumentsNamed())
                    return false;

                stringBuilder.Append("][");
                if (!TryAppendMultidimensionalAndJagged(jaggedElement))
                    return false;
            }

            return true;
        }

        stringBuilder.Append("]");

        /* If here then success. Arrays themselves are arguments, so has arguments
         * is always set as the result on success. */
        argumentSearchResult = ArgumentSearchResult.HasArguments;

        return stringBuilder.ToString();
    }

    /// <summary>
    /// Returns the NamedTypeSymbol full name with arguments.
    /// </summary>
    /// <example>RootNamespace.Strings.StringBuffer<int></example>
    public static string GetNamedTypeSymbolFullNameWithArguments(this INamedTypeSymbol namedTypeSymbol, ArgumentSearchType argumentSearchType, out ArgumentSearchResult argumentSearchResult)
    {
        //Default value until changed.
        string typeFullName = namedTypeSymbol.GetTypeSymbolFullName();

        List<string> results = [];

        //Type does not have arguments.
        if (!namedTypeSymbol.IsGenericType)
        {
            argumentSearchResult = ArgumentSearchResult.NoArguments;
            return typeFullName;
        }

        int typeParameterCount = 0;

        if (argumentSearchType is ArgumentSearchType.ExplicitlyNamed && !namedTypeSymbol.ArePresentArgumentsNamed())
        {
            argumentSearchResult = ArgumentSearchResult.ErrorForSearchType;
            return string.Empty;
        }

        argumentSearchResult = ArgumentSearchResult.HasArguments;

        foreach (ITypeSymbol typeArgument in namedTypeSymbol.TypeArguments)
        {
            if (argumentSearchType is ArgumentSearchType.Generic || typeArgument.TypeKind is TypeKind.TypeParameter)
                results.Add($"T{typeParameterCount++}");
            else if (typeArgument is INamedTypeSymbol argumentNamedTypeSymbol)
                results.Add(argumentNamedTypeSymbol.GetNamedTypeSymbolFullNameWithArguments(argumentSearchType, out argumentSearchResult));
            else if (typeArgument is IArrayTypeSymbol argumentArrayTypeSymbol)
                results.Add(argumentArrayTypeSymbol.GetArrayTypeSymbolFullNameWithArguments(argumentSearchType, out argumentSearchResult));
            else
                argumentSearchResult = ArgumentSearchResult.ErrorForSearchType;

            //If search result has switched to error.
            if (argumentSearchResult.HasError())
                return string.Empty;
        }

        /* If here there is no error. */
        argumentSearchResult = results.Count > 0 ? ArgumentSearchResult.HasArguments : ArgumentSearchResult.NoArguments;

        return $"{typeFullName}{results.GetCombinedArguments()}";
    }

    /// <summary>
    /// Returns arguments as a list.
    /// </summary>
    public static List<Argument> GetTypeSymbolArguments(this INamedTypeSymbol namedTypeSymbol, ArgumentSearchType argumentSearchType, out ArgumentSearchResult argumentSearchResult)
    {
        List<Argument> results = [];

        //Type does not have arguments.
        if (!namedTypeSymbol.IsGenericType)
        {
            argumentSearchResult = ArgumentSearchResult.NoArguments;
            return results;
        }

        int typeParameterCount = 0;

        if (argumentSearchType is ArgumentSearchType.ExplicitlyNamed && !namedTypeSymbol.ArePresentArgumentsNamed())
        {
            argumentSearchResult = ArgumentSearchResult.ErrorForSearchType;
            return results;
        }

        argumentSearchResult = ArgumentSearchResult.HasArguments;

        foreach (ITypeSymbol typeArgument in namedTypeSymbol.TypeArguments)
        {
            if (argumentSearchType is ArgumentSearchType.Generic || typeArgument.TypeKind is TypeKind.TypeParameter)
                results.Add(new(typeArgument, $"T{typeParameterCount++}", isNamed: false));
            else if (typeArgument is INamedTypeSymbol argumentNamedTypeSymbol)
                results.Add(new(typeArgument, argumentNamedTypeSymbol.GetNamedTypeSymbolFullNameWithArguments(argumentSearchType, out argumentSearchResult), isNamed: true));
            else
                argumentSearchResult = ArgumentSearchResult.ErrorForSearchType;

            //If search result has switched to error.
            if (argumentSearchResult.HasError())
                return results;
        }

        /* If here there is no error. */
        argumentSearchResult = results.Count > 0 ? ArgumentSearchResult.HasArguments : ArgumentSearchResult.NoArguments;

        return results;
    }

    /// <summary>
    /// Returns true if a TypeSymbol has arguments.
    /// </summary>
    public static bool HasArguments(this ITypeSymbol symbol)
    {
        if (symbol is IArrayTypeSymbol)
            return true;

        if (symbol is INamedTypeSymbol { IsGenericType: true })
            return true;

        return false;
    }

    /// <summary>
    /// Returns true if any present arguments are named.
    /// </summary>
    public static bool ArePresentArgumentsNamed(this ITypeSymbol symbol)
    {
        if (symbol is IArrayTypeSymbol arrayTypeSymbol)
            return arrayTypeSymbol.ElementType.TypeKind is not TypeKind.TypeParameter;

        if (symbol is INamedTypeSymbol { IsGenericType: true } namedTypeSymbol)
        {
            foreach (ITypeSymbol typeArgument in namedTypeSymbol.TypeArguments)
            {
                if (typeArgument.TypeKind is TypeKind.TypeParameter)
                    return false;
            }
        }

        //No arguments, return true.
        return true;
    }

    /// <summary>
    /// Returns the short name of a symbol which includes the namespace.
    /// </summary>
    public static bool TypeSymbolImplementsInterface(this ITypeSymbol symbol, string? interfaceFullName)
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

    public static bool IsUserDefinedEnumClassOrStruct(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.IsUserDefinedEnum() || typeSymbol.IsUserDefinedClass() || typeSymbol.IsUserDefinedStruct();
    }

    public static bool IsUserDefinedClassOrStruct(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.IsUserDefinedClass() || typeSymbol.IsUserDefinedStruct();
    }

    public static bool IsUserDefinedStruct(this ITypeSymbol typeSymbol)
    {
        return typeSymbol is { TypeKind: TypeKind.Struct, SpecialType: SpecialType.None };
    }

    public static bool IsUserDefinedClass(this ITypeSymbol typeSymbol)
    {
        return typeSymbol is { TypeKind: TypeKind.Class, SpecialType: SpecialType.None };
    }

    public static bool IsUserDefinedEnum(this ITypeSymbol typeSymbol)
    {
        return typeSymbol is { TypeKind: TypeKind.Enum, SpecialType: SpecialType.None };
    }

    public static bool IsClass(this ITypeSymbol typeSymbol)
    {
        return typeSymbol is { TypeKind: TypeKind.Class };
    }

    public static bool IsStruct(this ITypeSymbol typeSymbol)
    {
        return typeSymbol is { TypeKind: TypeKind.Struct };
    }

    public static bool IsClassOrStruct(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.IsStruct() || typeSymbol.IsClass();
    }

    /// <summary>
    /// Returns if a TypeSymbol is a reference type or is declared as nullable.
    /// </summary>
    /// <returns></returns>
    public static bool CanBeNull(this ITypeSymbol typeSymbol)
    {
        if (typeSymbol.IsReferenceType)
            return true;

        if (typeSymbol.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return true;

        if (typeSymbol.TypeKind is TypeKind.Pointer)
            return true;

        return typeSymbol is ITypeParameterSymbol { IsReferenceType: true };
    }

    /// <summary>
    /// Returns if a TypeSymbol is encapsulated in Nullable<> is encapsulated in Nullable<> and the encapsulated type is an INamedTypeSymbol.
    /// </summary>
    /// <returns></returns>
    public static bool TryGetNullableEncapsulatedNamedTypeSymbol(this ITypeSymbol typeSymbol, out ITypeSymbol encapsulatedTypeSymbol)
    {
        encapsulatedTypeSymbol = null;
        
        if (!typeSymbol.IsNullable())
            return false;

        if (typeSymbol is not INamedTypeSymbol namedTypeSymbol)
            return false;

        if (namedTypeSymbol.TypeArguments.Length == 0)
            return false;

        encapsulatedTypeSymbol = namedTypeSymbol.TypeArguments[0];
        if (encapsulatedTypeSymbol is not INamedTypeSymbol)
            return false;

        return true;
    }

    /// <summary>
    /// Returns if the Symbol is a primitive Type using SpecialType.
    /// </summary>
    /// <param name="symbol">Symbol to check.</param>
    /// <returns>True if primitive.</returns>
    public static bool IsPrimitive(this ITypeSymbol symbol)
    {
        switch (symbol.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Char:
                return true;
            default:
                return false;
        }
    }
    /// <summary>
    /// Returns if a TypeSymbol is encapsulated in Nullable<>. 
    /// </summary>
    /// <returns></returns>
    public static bool IsNullable(this ITypeSymbol typeSymbol) => typeSymbol.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    /// <summary>
    /// Returns a string as readable context (UserStruct.SomeField).
    /// </summary>
    public static string ToReadable(this ITypeSymbol typeSymbol) => ToReadable(typeSymbol, fieldSymbol: null);

    /// <summary>
    /// Returns a string as readable context (UserStruct.SomeField).
    /// </summary>
    public static string ToReadable(this ITypeSymbol typeSymbol, IFieldSymbol fieldSymbol)
    {
        StringBuilder stringBuilder = new();

        stringBuilder.Append(typeSymbol.GetTypeSymbolFullName());
        if (fieldSymbol is not null)
            stringBuilder.Append($".{fieldSymbol.GetSymbolFullName()}");

        return stringBuilder.ToString();
    }
}
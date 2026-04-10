#nullable enable
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace CodeAnalysis.Extensions;

public static class AssemblySymbolExtensions
{
    /// <summary>
    /// Recursively gets INamespaceSymbols within an assembly.
    /// </summary>
    public static List<INamespaceSymbol> RecursivelyGetNamespaceSymbols(this IAssemblySymbol assemblySymbol)
    {
        if (assemblySymbol is null)
            return new();

        List<INamespaceSymbol> allNamespaces = new();

        // Get the global namespace and add it to begin iteration.
        INamespaceSymbol globalNamespace = assemblySymbol.GlobalNamespace;
        allNamespaces.Add(globalNamespace);

        for (int i = 0; i < allNamespaces.Count; i++)
        {
            INamespaceSymbol current = allNamespaces[i];
            allNamespaces.AddRange(current.GetNamespaceMembers());
        }

        return allNamespaces;
    }

    /// <summary>
    /// Recursively gets INamedTypeSymbols within an assembly.
    /// </summary>
    public static List<INamedTypeSymbol> RecursivelyGetNamedTypeSymbols(this IAssemblySymbol assemblySymbol)
    {
        List<INamespaceSymbol> allNamespaces = assemblySymbol.RecursivelyGetNamespaceSymbols();

        List<INamedTypeSymbol> namedTypeSymbols = new();
        foreach (INamespaceSymbol namespaceSymbol in allNamespaces)
            namedTypeSymbols.AddRange(namespaceSymbol.GetTypeMembers());

        // Now recursively iterate namedTypeSymbols.
        for (int i = 0; i < namedTypeSymbols.Count; i++)
            namedTypeSymbols.AddRange(namedTypeSymbols[i].GetTypeMembers());

        return namedTypeSymbols;
    }

    /// <summary>
    /// Recursively gets IMethodSymbols within an assembly.
    /// </summary>
    public static List<IMethodSymbol> RecursivelyGetMethodSymbols(this IAssemblySymbol assemblySymbol, Accessibility? requiredAccessibility = null)
    {
        List<INamedTypeSymbol> namedTypeSymbols = assemblySymbol.RecursivelyGetNamedTypeSymbols();
        
        List<IMethodSymbol> methodSymbols = [];
        
        foreach (INamedTypeSymbol namedTypeSymbol in namedTypeSymbols)
            methodSymbols.AddRange(namedTypeSymbol.GetMethodSymbols(requiredAccessibility));
        
        return methodSymbols;
    }
}
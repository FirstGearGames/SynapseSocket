using System.Collections.Generic;
using System.Text;
using CodeAnalysis.Constants;
using Microsoft.CodeAnalysis;
using CodeAnalysis.Extensions;

namespace CodeAnalysis.SourceGenerators.CodeBuilding;

public static class CodeBuilder
{
    private static StringBuilder _stringBuilder = new();

    /// <summary>
    /// Gets the delcared accessibility of a method and returns it as string to use in code.
    /// </summary>
    /// <returns></returns>
    public static string GetDeclaredAccessibility(this IMethodSymbol symbol)
    {
        return symbol.DeclaredAccessibility switch
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

    /// <summary>
    /// Creates a class optionally wrapping it in a namespace.
    /// </summary>
    public static string CreatePublicStaticClass(string className, out string footer, string namespaceName = "")
    {
        _stringBuilder.Clear();

        int indent = 0;
        bool hasNamespace = namespaceName.Length > 0;
        if (hasNamespace)
        {
            _stringBuilder.AppendLine($"namespace {namespaceName}");
            _stringBuilder.AppendLine("{");
            indent++;
        }

        _stringBuilder.AppendLine(indent, $"public static class {className}");
        _stringBuilder.AppendLine(indent, "{");

        StringBuilder footerSb = new();

        if (hasNamespace)
        {
            footerSb.AppendLine(indent, "}");
            footerSb.Append('}');
        }

        footer = footerSb.ToString();
        return _stringBuilder.ToString();
    }

    /// <summary>
    /// Creates a class optionally wrapping it in a namespace.
    /// </summary>
    public static string CreateClassCopy(INamedTypeSymbol originalClassNamedTypeSymbol, out string footer, string namespaceName = "")
    {
        _stringBuilder.Clear();

        int indent = 0;
        bool hasNamespace = namespaceName.Length > 0;
        if (hasNamespace)
        {
            _stringBuilder.AppendLine($"namespace {namespaceName}");
            _stringBuilder.AppendLine("{");
            indent++;
        }

        _stringBuilder.AppendLine(indent, originalClassNamedTypeSymbol.GetClassOrStructHeader());
        _stringBuilder.AppendLine(indent, "{");

        StringBuilder footerSb = new();

        if (hasNamespace)
        {
            footerSb.AppendLine(indent, "}");
            footerSb.Append('}');
        }

        footer = footerSb.ToString();
        return _stringBuilder.ToString();
    }

    /// <summary>
    /// Calls a method taking optional arguments.
    /// </summary>
    public static string CallMethod(string methodName, string callingVariable = "", List<string>? variableNames = null)
    {
        if (callingVariable.Length > 0)
            callingVariable += ".";

        _stringBuilder.Clear();
        _stringBuilder.Append($"{callingVariable}{methodName}(");

        //Add arguments.
        if (variableNames is not null)
            _stringBuilder.Append(string.Join(", ", variableNames));

        //End call.
        _stringBuilder.Append(')');

        return _stringBuilder.ToString();
    }

    /// <summary>
    /// Creates a multiline if statement conditional.
    /// </summary>
    public static string CreateMultiLineIf(int indent, string conditionaltext, string line)
    {
        StringBuilder sb = new();
        sb.Append(indent + 1, line);
        return CreateMultiLineIf(indent, conditionaltext, sb);
    }

    /// <summary>
    /// Creates a multiline if statement conditional.
    /// </summary>
    public static string CreateMultiLineIf(int indent, string conditionaltext, StringBuilder lines)
    {
        _stringBuilder.Clear();
        _stringBuilder.AppendLine(indent, $"if ({conditionaltext})");
        _stringBuilder.AppendLine(indent, "{");
        _stringBuilder.AppendLine(lines.ToString());
        _stringBuilder.AppendLine(indent, "}");
        return _stringBuilder.ToString();
    }

    public static string CreateLocalVariable(string fullTypeName, string variableName, string defaultValue = "", bool closeLine = true)
    {
        _stringBuilder.Clear();
        _stringBuilder.Append($"{fullTypeName} {variableName}");
        string lineCloser = closeLine ? ";" : string.Empty;
        if (defaultValue.Length > 0)
            _stringBuilder.Append($" = {defaultValue}{lineCloser}");
        else
            _stringBuilder.Append(lineCloser);

        return _stringBuilder.ToString();
    }

    public static string CreateFunction(string returnType, params string[] types)
    {
        _stringBuilder.Clear();

        _stringBuilder.Append($"new {NativeConstants.FuncFullName}<");
        foreach (string item in types)
            _stringBuilder.Append($"{item}, ");

        _stringBuilder.Append($"{returnType}>");

        return _stringBuilder.ToString();
    }

    public static string CreateAction(params string[] types)
    {
        _stringBuilder.Clear();

        _stringBuilder.Append($"new {NativeConstants.ActionFullName}<");
        for (int i = 0; i < types.Length; i++)
        {
            _stringBuilder.Append($"{types[i]}");
            if (i < types.Length - 1)
                _stringBuilder.Append(", ");
        }

        _stringBuilder.Append($">");

        return _stringBuilder.ToString();
    }

}
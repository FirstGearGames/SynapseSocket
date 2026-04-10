using System;
using System.Text;

namespace CodeAnalysis.Extensions;

public static class StringBuilderExtensions
{
    public static StringBuilder Indent(this StringBuilder stringBuilder, int count = 1)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));

        return count switch
        {
            0 => stringBuilder,
            1 => stringBuilder.Append('\t'),
            _ => stringBuilder.Append('\t', count)
        };
    }

    public static void AppendDoubleLine(this StringBuilder stringBuilder, string text) 
    {
        stringBuilder.AppendLine(text);
        stringBuilder.AppendLine();
    }

    public static void AppendLineWithOpeningBracket(this StringBuilder stringBuilder, string text) 
    {
        stringBuilder.AppendLine(text);
        stringBuilder.AppendLine("{");
    }

    public static void AppendLineWithClosingBracket(this StringBuilder stringBuilder, string text, bool doubleReturn) 
    {
        stringBuilder.AppendLine(text);
        stringBuilder.AppendLine("}");
        if (doubleReturn)
            stringBuilder.AppendLine(string.Empty);
    }

    public static void AppendLineWithClosingBracket(this StringBuilder stringBuilder, bool doubleReturn) 
    {
        stringBuilder.AppendLine("}");
        if (doubleReturn)
            stringBuilder.AppendLine(string.Empty);
    }

        
    public static void Append(this StringBuilder stringBuilder, int indentCount, string text)
    {
        stringBuilder.Indent(indentCount).Append(text);
    }

    public static void AppendLine(this StringBuilder stringBuilder, int indentCount, string text)
    {
        stringBuilder.Indent(indentCount).AppendLine(text);
    }

    public static void AppendThrowLine(this StringBuilder stringBuilder, int indentCount, string text)
    {
        stringBuilder.Indent(indentCount).AppendLine($"throw new Exception(\"{text}\");");
    }
}
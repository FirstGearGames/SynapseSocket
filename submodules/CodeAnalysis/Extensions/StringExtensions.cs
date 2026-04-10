using System;
using System.IO;
using System.Text;

namespace CodeAnalysis.Extensions;

public static class StringExtensions
{
    /// <summary>
    /// Value representing when an index is not found or specified.
    /// </summary>
    public const int UnsetIndex = -1;

    /// <summary>
    /// Formats a string with indents using opening and closing braces and the determining character.
    /// </summary>
    /// <param name="thisValue"></param>
    /// <returns></returns>
    public static string IndentByBrace(this string thisValue)
    {
        if (string.IsNullOrWhiteSpace(thisValue))
            return string.Empty;
            
        StringBuilder result = new();
        StringReader reader = new(thisValue);
            
        //Current indentation.
        int indentLevel = 0;
            
        while (reader.ReadLine() is { } readLine)
        {
            const string Indentation = "    ";

            // 1. Trim leading/trailing whitespace from the original line
            string trimmedLine = readLine.Trim();

            // Skip completely empty lines
            if (string.IsNullOrWhiteSpace(trimmedLine))
            {
                result.AppendLine();
                continue;
            }

            // 2. Adjust indent level BEFORE prepending for closing braces '}'
            // If the line starts with '}', decrease the indent before applying it
            if (trimmedLine.StartsWith("}"))
                indentLevel = Math.Max(0, indentLevel - 1);

            // 3. Prepend the current indent level
            for (int i = 0; i < indentLevel; i++)
                result.Append(Indentation);

            // 4. Append the trimmed content of the line
            result.AppendLine(trimmedLine);

            // 5. Adjust indent level AFTER prepending for opening braces '{'
            // If the line contains '{', increase the indent for the NEXT line
            if (trimmedLine.Contains("{"))
                indentLevel++;
        }


        return result.ToString();
    }

    public static string RemoveGlobalAlias(this string value)
    {
        if (value.StartsWith("global::"))
            value = value.Substring(8);
        else if (value.StartsWith("<global namespace>"))
            value = value.Substring(18);
        return value;
    }

    /// <summary>
    /// Converts a camelCase strings to PascalCase. Non-letter and non-numeric prefixes are removed.
    /// </summary>
    public static string CamelCaseToPascalCase(this string value)
    {
        int index = value.GetFirstLetterOrDigitIndex();

        //Index not found. String is null or has no chars/numbers.
        if (index == UnsetIndex)
            return value;

        char firstValidChar = value[index];

        //First character is not a letter, return as-is.
        if (!char.IsLetter(firstValidChar))
            return value;

        //Already capitalized.
        if (char.IsUpper(firstValidChar))
            return value;

        return $"{char.ToUpperInvariant(firstValidChar)}{value.Substring(index + 1)}";
    }

    /// <summary>
    /// Converts a pascal case string to member case with an optional prefix.
    /// </summary>
    /// <example>With a prefix of '_' value 'HelloWorld' is returned as '_helloWorld'.</example>
    /// <remarks>Prefix is only added if missing.</remarks>
    public static string PascalCaseToCamelCase(this string value, string prefix)
    {
        int index = value.GetFirstLetterOrDigitIndex();

        //Index not found. String is null or has no chars/numbers.
        if (index == UnsetIndex)
            return value;

        char firstValidChar = value[index];

        /* There are marginally more efficient ways to handle these prefix operations
         * but allocations are going to occur either way - use what is easier to read. */

        StringBuilder stringBuilder = new();
        int prefixLength = prefix.Length;

        //There is a prefix.
        if (prefixLength > 0)
        {
            if (prefixLength >= value.Length)
            {
                stringBuilder.Append(prefix);

                AppendLowerFirstCharAndRemainingValue();

                return stringBuilder.ToString();
            }

            //If prefix is not yet added then do so.
            if (value.Substring(0, prefixLength) != prefix)
                stringBuilder.Append(prefix);
        }

        //Add renaming with lowercase char.
        AppendLowerFirstCharAndRemainingValue();

        return stringBuilder.ToString();

        //Appends lowercase first char, and any renaming text in value.
        void AppendLowerFirstCharAndRemainingValue()
        {
            stringBuilder.Append(char.ToLowerInvariant(firstValidChar));
            //If value has enough length remaining append it as well.
            if (value.Length >= index)
                stringBuilder.Append(value.Substring(index + 1));
        }
    }


    /// <summary>
    /// Returns index of the first letter or number in a string.
    /// </summary>
    public static int GetFirstLetterOrDigitIndex(this string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return UnsetIndex;

        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsLetterOrDigit(value[i]))
                return i;
        }

        return UnsetIndex;
    }
        
    /// <summary>
    /// Makes names of various types safe for use as file names.
    /// </summary>
    /// <param name="value">Value to make safe.</param>
    /// <returns>Value with non-alphaNumeric characters replaced with '_'.</returns>
    public static string MakeFileSafeName(this string value)
    {
        StringBuilder stringBuilder = new();
            
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
                stringBuilder.Append(c);
            else
                stringBuilder.Append('_');
        }

        return stringBuilder.ToString();
    }

        
}
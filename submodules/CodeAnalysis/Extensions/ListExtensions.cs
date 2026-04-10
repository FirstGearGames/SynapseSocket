using System.Collections.Generic;
using System.Text;

namespace CodeAnalysis.Extensions;

public static class ListExtensions
{
    /// <summary>
    /// Combines into a natural string: <str0, str1, str2 ...>
    /// </summary>
    public static string GetCombinedArguments(this List<Argument> argumentList)
    {
        if (argumentList is null || argumentList.Count == 0)
            return string.Empty;

        StringBuilder stringBuilder = new();

        foreach (Argument methodArgument in argumentList)
        {
            // Add separate if argument already exists.
            if (stringBuilder.Length != 0)
                stringBuilder.Append(", ");

            stringBuilder.Append(methodArgument.Name);
        }

        return $"<{stringBuilder}>";
    }

    /// <summary>
    /// Combines into a natural string: <str0, str1, str2 ...>
    /// </summary>
    public static string GetCombinedArguments(this List<string> stringList)
    {
        if (stringList is null || stringList.Count == 0)
            return string.Empty;

        StringBuilder stringBuilder = new();

        foreach (string argumentName in stringList)
        {
            // Add separate if argument already exists.
            if (stringBuilder.Length != 0)
                stringBuilder.Append(", ");

            stringBuilder.Append(argumentName);
        }

        return $"<{stringBuilder}>";
    }
}
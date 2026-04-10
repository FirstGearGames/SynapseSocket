using System;
using Microsoft.CodeAnalysis;

namespace CodeAnalysis.Extensions;

public static class EnumExtensions
{
    /// <summary>
    /// Gets the maximum value of an enum, combined if flags or individual if not.
    /// </summary>
    /// <remarks>A 0 value is returned if type is not an enum.</remarks>
    public static long GetMaximumEnumValue(this INamedTypeSymbol nameTypeSymbol)
    {
        if (nameTypeSymbol.TypeKind != TypeKind.Enum)
            return 0;

        bool isFlagsEnum = nameTypeSymbol.HasAttribute(SearchScope.Exact, typeof(System.FlagsAttribute), out _);

        long largestMemberValue = long.MinValue;
        long asFlagsValue = 0;

        //Becomes true if any member has been checked.
        bool hasAnyMemberBeenHandled = false;
            
        foreach (ISymbol member in nameTypeSymbol.GetMembers())
        {
            // A const fieldSymbol is expected.
            if (member is not IFieldSymbol { IsConst: true } fieldSymbol)
                continue;

            object? constantValue = fieldSymbol.ConstantValue;

            if (constantValue is null)
                continue;
                
            // Convert value to support signed and unsigned.
            long value = Convert.ToInt64(constantValue);

            //Set the largest value.
            if (!hasAnyMemberBeenHandled || value > largestMemberValue)
                largestMemberValue = value;
                
            //If flags then combine with past values.
            if (isFlagsEnum)
                asFlagsValue |= value;

            hasAnyMemberBeenHandled = true;
        }

        return isFlagsEnum ? asFlagsValue : largestMemberValue;
    }

    /// <summary>
    /// Returns the highest numeric value for T.
    /// </summary>
    public static int GetMaximumValue<T0>() where T0 : Enum
    {
        Type enumType = typeof(T0);
        /* Brute force enum values.
         * Linq Last/Max lookup throws for IL2CPP. */
        int maximumValue = 0;
        Array pidValues = Enum.GetValues(enumType);
        foreach (T0 pid in pidValues)
        {
            object obj = Enum.Parse(enumType, pid.ToString());
            int value = Convert.ToInt32(obj);
            maximumValue = Math.Max(maximumValue, value);
        }

        return maximumValue;
    }
        
                
    /// <summary>
    /// Gets all values for an enum.
    /// </summary>
    public static T0[] GetValuesAllocated<T0>() where T0 : Enum
    {
        /* Optimized over LINQ, and compatible
         * with lower .NET 2+. */
        return (T0[])Enum.GetValues(typeof(T0));
    }


}
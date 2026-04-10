namespace CodeAnalysis.Constants;

public static class NativeConstants
{
    /// <summary>
    /// void when being used as a return type.
    /// </summary>
    public const string FuncFullName = "System.Func";
    public const string ActionFullName = "System.Action";
    public const string BooleanFullName = "System.Boolean";
    public const string UInt64FullName = "System.UInt64";
    public const string ObjectFullName = "System.Object";
    public const string LineFeed = "\r\n";
    public const string FirstGenericParameterName = $"{GenericParameterNamePrefix}0";
    public const string GenericParameterNamePrefix = "T";
    public const string GenericArrayFullName = $"{FirstGenericParameterName}[]";
    public const string ListFullName = "System.Collections.Generic.List";
    public const string GenericListFullName = $"{ListFullName}<{FirstGenericParameterName}>";
}
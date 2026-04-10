namespace CodeAnalysis.Finding;

public enum NameType
{
    /// <summary>
    /// Name including namespace and containing type.
    /// </summary>
    FullName,
    /// <summary>
    /// Name excluding namespace and containing type.
    /// </summary>
    ShortName,
}

public static class NameTypeExtensions
{
    public static bool IsFullName(this NameType thisValue) => thisValue == NameType.FullName;
    public static bool IsShortName(this NameType thisValue) => thisValue == NameType.ShortName;
}
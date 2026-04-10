namespace CodeAnalysis.Finding;

public enum ArgumentSearchType
{
    /// <summary>
    /// Will return arguments as named when possible (bool, string).
    /// </summary>
    PreferNamed,
    /// <summary>
    /// Returns arguments only if they are named.
    /// </summary>
    ExplicitlyNamed,
    /// <summary>
    /// Returns arguments as generic (T0, T1).
    /// </summary>
    Generic
}

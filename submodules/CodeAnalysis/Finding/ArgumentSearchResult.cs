namespace CodeAnalysis.Finding;


[System.Flags]
public enum ArgumentSearchResult
{
    /// <summary>
    /// Used when the search type rules could not be met.
    /// </summary>
    ErrorForSearchType = 1 << 0,
    /// <summary>
    /// Used when the type has arguments and the search type rules could be met.
    /// </summary>
    HasArguments = 1 << 1,
    /// <summary>
    /// Used when the type does not have any arguments.
    /// </summary>
    NoArguments = 1 << 2,
}


namespace CodeAnalysis.Extensions;

public enum SearchScope 
{
    /// <summary>
    /// Matches only the specified type.
    /// </summary>
    Exact,
    /// <summary>
    /// Matches the specified type or any of its base types.
    /// </summary>
    Hierarchy,
}
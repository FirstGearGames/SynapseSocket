using CodeAnalysis.Finding;

namespace CodeAnalysis.Extensions;


public static class ArgumentSearchResultExtensions
{
    public static bool HasError(this ArgumentSearchResult thisArgumentSearchResult) => thisArgumentSearchResult.HasFlag(ArgumentSearchResult.ErrorForSearchType);
}

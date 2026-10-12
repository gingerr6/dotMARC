namespace DotMarc.Api;

/// <summary>A query filter's description and, for a fixed set, its allowed values, for the OpenAPI document.</summary>
public sealed record ApiFilterDocs(string Name, string Description, IReadOnlyList<string>? Values)
{
    public const string ListHint = " Repeat it or separate values with commas.";
}

public static class ApiFilterDocsExtensions
{
    public static RouteHandlerBuilder DocumentsFilter(this RouteHandlerBuilder builder, string name, string description, IEnumerable<string>? values = null) =>
        builder.WithMetadata(new ApiFilterDocs(name, description, values?.ToList()));
}

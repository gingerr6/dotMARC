using Microsoft.OpenApi;

namespace DotMarc.Api;

/// <summary>A query filter's description and, for a fixed set, its allowed values, for the OpenAPI document. Filters are
/// read as text so a bad value gets an error naming the filter; <see cref="Type"/> says what the value really is (or each
/// item of a list filter), and <see cref="Defaults"/> what applies when it's left out.</summary>
public sealed record ApiFilterDocs(string Name, string Description, IReadOnlyList<string>? Values, JsonSchemaType? Type = null, IReadOnlyList<string>? Defaults = null)
{
    public const string ListHint = " Repeat it or separate values with commas.";
}

public static class ApiFilterDocsExtensions
{
    public static RouteHandlerBuilder DocumentsFilter(this RouteHandlerBuilder builder, string name, string description, IEnumerable<string>? values = null,
        JsonSchemaType? type = null, IEnumerable<string>? defaults = null) =>
        builder.WithMetadata(new ApiFilterDocs(name, description, values?.ToList(), type, defaults?.ToList()));
}

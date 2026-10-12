using System.Security.Claims;
using DotMarc.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

public static class GroupAndTagEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/groups", ListGroupsAsync)
            .RequirePermission(Permission.GroupsView)
            .WithTags(ApiTags.GroupsAndTags)
            .WithName("ListGroups")
            .WithSummary("List groups")
            .WithDescription("Every group this key can see, with how many domains are in each. A key limited to certain groups sees only those.")
            .DocumentsFilter("search", "Part of the name, in any letter case.");

        api.MapGet("/tags", ListTagsAsync)
            .RequirePermission(Permission.TagsView)
            .WithTags(ApiTags.GroupsAndTags)
            .WithName("ListTags")
            .WithSummary("List tags")
            .WithDescription("Every tag, with its colour and how many domains this key can see carry it.")
            .DocumentsFilter("search", "Part of the name, in any letter case.");
    }

    private static async Task<Ok<List<ApiGroup>>> ListGroupsAsync(string? search, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var visibleGroups = ApiScope.From(user).Groups(context.Groups.AsNoTracking());
        if (LikePattern.Contains(search) is { } pattern)
        {
            visibleGroups = visibleGroups.Where(group => EF.Functions.ILike(group.Name, pattern, LikePattern.Escape));
        }

        var groups = await visibleGroups
            .OrderBy(group => group.Name)
            .Select(group => new ApiGroup(group.Id, group.Name, group.Domains.Count))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(groups);
    }

    private static async Task<Ok<List<ApiTag>>> ListTagsAsync(string? search, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var allTags = context.Tags.AsNoTracking();
        if (LikePattern.Contains(search) is { } pattern)
        {
            allTags = allTags.Where(tag => EF.Functions.ILike(tag.Name, pattern, LikePattern.Escape));
        }

        var tags = await allTags
            .OrderBy(tag => tag.Name)
            .Select(tag => new { tag.Id, tag.Name, tag.Color, DomainIds = tag.Domains.Select(domain => domain.Id).ToList() })
            .ToListAsync(cancellationToken);
        var visibleDomainIds = (await scope.Domains(context.Domains.AsNoTracking()).Select(domain => domain.Id).ToListAsync(cancellationToken)).ToHashSet();
        return TypedResults.Ok(tags
            .Select(tag => new ApiTag(tag.Id, tag.Name, tag.Color.ToString(), tag.DomainIds.Count(visibleDomainIds.Contains)))
            .ToList());
    }
}

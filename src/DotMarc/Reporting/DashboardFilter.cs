using System.Globalization;

namespace DotMarc.Reporting;

/// <summary>Which Group and Tag the dashboard should show. The address wins when it names one (a shared link, or the
/// dashboard after it has written the filter into the address). The home page and the Dashboard button both open
/// <c>/dashboard</c> with no filter, so the one remembered from earlier in this visit applies instead. An id that
/// isn't in the list any more (the Group was removed) is ignored. Sorting the names does not come into it: the
/// choice is the id, never the position in the list.</summary>
public static class DashboardFilter
{
    public static (int? GroupId, int? TagId) Resolve(
        int? queryGroupId, int? queryTagId,
        int? rememberedGroupId, int? rememberedTagId,
        IEnumerable<int> groupIds, IEnumerable<int> tagIds,
        bool idsAreKnown = true)
    {
        // A Group or Tag in the address is the whole choice. Filling the other one in from memory would
        // surprise someone who opened a link for a single Group.
        var useQuery = queryGroupId is not null || queryTagId is not null;
        var groupId = useQuery ? queryGroupId : rememberedGroupId;
        var tagId = useQuery ? queryTagId : rememberedTagId;
        if (!idsAreKnown)
        {
            // The Group and Tag lists have not been read yet. Keep the id and drop it after the read if it's gone.
            return (groupId, tagId);
        }

        var groups = groupIds as IReadOnlySet<int> ?? groupIds.ToHashSet();
        var tags = tagIds as IReadOnlySet<int> ?? tagIds.ToHashSet();
        return (Known(groupId, groups), Known(tagId, tags));
    }

    /// <summary>The domains that belong to the chosen Group and carry the chosen Tag. Either one being unset
    /// leaves that side open, so a Group on its own is every domain in the Group.</summary>
    public static List<T> Matching<T>(IEnumerable<T> domains, int? groupId, int? tagId, Func<T, IEnumerable<int>> groupIds, Func<T, IEnumerable<int>> tagIds) =>
        domains.Where(domain =>
                (groupId is null || groupIds(domain).Contains(groupId.Value))
                && (tagId is null || tagIds(domain).Contains(tagId.Value)))
            .ToList();

    /// <summary>The dashboard address for a Group and Tag, with no filter left off so a cleared dropdown really
    /// does show every domain.</summary>
    public static string Address(int? groupId, int? tagId)
    {
        var parts = new List<string>(2);
        if (groupId is { } group)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"group={group}"));
        }
        if (tagId is { } tag)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"tag={tag}"));
        }

        return parts.Count == 0 ? "/dashboard" : "/dashboard?" + string.Join('&', parts);
    }

    private static int? Known(int? id, IReadOnlySet<int> ids) => id is { } value && ids.Contains(value) ? value : null;
}

/// <summary>The Group and Tag the dashboard dropdowns show, and whether the domain list was loaded for that pair.
/// Choosing a Group (including restoring one when the page comes back) marks the list as stale. The page must not
/// keep showing it: that is the list of every domain, with the Group's name in the dropdown.</summary>
public sealed class DashboardFilterSession
{
    public int? GroupId { get; private set; }
    public int? TagId { get; private set; }

    /// <summary>True only after <see cref="MarkLoaded"/> for the current <see cref="GroupId"/> and <see cref="TagId"/>.</summary>
    public bool ListMatchesSelection { get; private set; }

    public void Select(int? groupId, int? tagId)
    {
        if (groupId == GroupId && tagId == TagId)
        {
            return;
        }

        GroupId = groupId;
        TagId = tagId;
        ListMatchesSelection = false;
    }

    public void MarkLoaded() => ListMatchesSelection = true;
}

/// <summary>The dashboard filter for this Blazor circuit. Moving to another page and back, including the home page
/// which only redirects to the dashboard, keeps the circuit and this object, but not the page. The new page reads
/// it before loading domains, instead of painting every domain and filling the dropdown in afterwards.</summary>
public sealed class DashboardFilterMemory
{
    public int? GroupId { get; private set; }
    public int? TagId { get; private set; }

    /// <summary>False until this circuit has chosen a filter, including choosing none. A fresh circuit must not
    /// treat "not read yet" as "show every domain" and skip the browser's remembered filter.</summary>
    public bool IsKnown { get; private set; }

    public void Remember(int? groupId, int? tagId)
    {
        GroupId = groupId;
        TagId = tagId;
        IsKnown = true;
    }
}

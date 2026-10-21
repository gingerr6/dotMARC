using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Setup;

/// <summary>What the New client wizard asks for.</summary>
public sealed record NewClientRequest(string Name, IReadOnlyList<string> Domains, bool ClientManagesDns, IReadOnlyList<string> PortalEmails);

/// <summary>What the wizard did. Problems are things it skipped, for the person to see; nothing is half-done for them.</summary>
public sealed record NewClientResult(int? GroupId, string? Refusal, IReadOnlyList<string> AddedDomains, IReadOnlyList<string> MovedDomains,
    IReadOnlyList<string> PortalUsers, IReadOnlyList<string> Problems);

/// <summary>Sets up a new client in one go: their Group, their domains in it, and the people who'll see them in the client
/// portal. Each step is one of the usual audited changes, made as the person running the wizard.</summary>
public static class ClientOnboarding
{
    /// <summary>The built-in role portal users get: Viewer, limited to the client's Group.</summary>
    public const string PortalRoleName = "Viewer";

    public static async Task<NewClientResult> CreateAsync(DotMarcDbContext context, AuditActor actor, NewClientRequest request, bool mayGrantAccess,
        CancellationToken cancellationToken = default)
    {
        var name = request.Name.Trim();
        switch (await GroupManagementService.AddGroupAsync(context, actor, name, cancellationToken).ConfigureAwait(false))
        {
            case GroupManagementService.AddGroupResult.InvalidName:
                return Refused("Give the client a name.");
            case GroupManagementService.AddGroupResult.AlreadyExists:
                return Refused($"There's already a group called {name}. Add the domains to it on the Domains page instead.");
        }

        var group = await context.Groups.SingleAsync(candidate => candidate.Name == name, cancellationToken).ConfigureAwait(false);
        if (request.ClientManagesDns)
        {
            await GroupManagementService.SetClientManagesDnsAsync(context, actor, group.Id, true, cancellationToken).ConfigureAwait(false);
        }

        var added = new List<string>();
        var moved = new List<string>();
        var problems = new List<string>();
        foreach (var rawDomain in request.Domains.Select(domain => domain.Trim()).Where(domain => domain.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            switch (await DomainManagementService.AddDomainAsync(context, actor, rawDomain, cancellationToken, [group.Id]).ConfigureAwait(false))
            {
                case DomainManagementService.AddDomainResult.Added:
                    DomainNameValidator.TryNormalize(rawDomain, out var addedName);
                    added.Add(addedName);
                    break;
                case DomainManagementService.AddDomainResult.InvalidName:
                    problems.Add($"{rawDomain} isn't a domain name, so it wasn't added.");
                    break;
                default:
                    // Already monitored: it joins this client too, keeping any Groups it's in.
                    DomainNameValidator.TryNormalize(rawDomain, out var normalized);
                    var existing = await context.Domains.AsNoTracking().Include(domain => domain.Groups)
                        .SingleAsync(domain => domain.Name == normalized, cancellationToken).ConfigureAwait(false);
                    await GroupManagementService.SetDomainGroupsAsync(context, actor, existing.Id, [.. existing.Groups.Select(g => g.Id), group.Id], cancellationToken).ConfigureAwait(false);
                    moved.Add(existing.Name);
                    break;
            }
        }

        var portalUsers = new List<string>();
        var emails = request.PortalEmails.Select(email => email.Trim()).Where(email => email.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (emails.Count > 0 && !mayGrantAccess)
        {
            problems.Add("You can't give people access, so nobody was added to the portal.");
        }
        else if (emails.Count > 0)
        {
            var roleId = await context.Roles.Where(role => role.Name == PortalRoleName && role.IsScopable).Select(role => (int?)role.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            foreach (var email in emails)
            {
                if (roleId is null)
                {
                    problems.Add($"There's no {PortalRoleName} role that can be limited to groups, so {email} wasn't added to the portal.");
                    continue;
                }

                var granted = await UserAccessManagementService.GrantAccessAsync(context, actor, email, roleId.Value, [group.Id], cancellationToken).ConfigureAwait(false);
                if (granted != UserAccessManagementService.GrantAccessResult.Granted)
                {
                    problems.Add(granted == UserAccessManagementService.GrantAccessResult.AlreadyExists
                        ? $"{email} already has access, so it wasn't changed. Add {name} to their groups on the Access page."
                        : $"{email} isn't an email address, so it wasn't added.");
                    continue;
                }

                var accessId = await context.UserAccesses.Where(access => access.Email == email).Select(access => access.Id).SingleAsync(cancellationToken).ConfigureAwait(false);
                await UserAccessManagementService.SetClientPortalAsync(context, actor, accessId, true, cancellationToken).ConfigureAwait(false);
                portalUsers.Add(email);
            }
        }

        return new NewClientResult(group.Id, null, added, moved, portalUsers, problems);

        static NewClientResult Refused(string reason) => new(null, reason, [], [], [], []);
    }
}

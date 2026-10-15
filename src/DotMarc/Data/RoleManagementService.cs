using DotMarc.Audit;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.Data;

/// <summary>Add/update/remove operations for Role rows, created through the "Manage access"
/// page. Follows this project's DomainManagementService convention of a static class operating
/// directly on a caller-supplied DotMarcDbContext.</summary>
public static class RoleManagementService
{
    public enum AddRoleResult { Added, InvalidName, AlreadyExists, NotAllowedWhenLimited }
    public enum UpdateRoleResult { Updated, InvalidName, AlreadyExists, Locked, NotAllowedWhenLimited }
    public enum RemoveRoleResult { Removed, Locked, InUse }
    public enum SetLimitedToGroupsResult { Updated, Locked, InUse, NotAllowedWhenLimited }

    /// <summary>A role limited to Groups (IsScopable) can be granted with Groups, and then sees and changes only the
    /// domains in them. It may only hold the permissions in GroupLimits.</summary>
    public static async Task<AddRoleResult> AddRoleAsync(DotMarcDbContext context, AuditActor actor, string rawName, List<Permission> permissions, CancellationToken cancellationToken = default, bool limitedToGroups = false)
    {
        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return AddRoleResult.InvalidName;
        }

        if (limitedToGroups && !GroupLimits.AllAllowed(permissions))
        {
            return AddRoleResult.NotAllowedWhenLimited;
        }

        var exists = await context.Roles.AnyAsync(r => r.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return AddRoleResult.AlreadyExists;
        }

        var role = new Role { Name = name, IsLocked = false, IsScopable = limitedToGroups, Permissions = permissions };
        context.Roles.Add(role);

        try
        {
            await AuditLog.SaveAndRecordAsync(context,
                () => AuditLog.Record(context, actor, AuditActions.RoleAdded, AuditTarget.For(role), $"Added role {role.Name}",
                    new AuditChanges().Field("Limited to groups", false, limitedToGroups).Set("Permissions", [], PermissionNames(permissions))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return AddRoleResult.AlreadyExists;
        }

        return AddRoleResult.Added;
    }

    public static async Task<UpdateRoleResult> UpdateRoleAsync(DotMarcDbContext context, AuditActor actor, int roleId, string rawName, List<Permission> permissions, CancellationToken cancellationToken = default)
    {
        var role = await context.Roles.SingleAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role.IsLocked)
        {
            return UpdateRoleResult.Locked;
        }

        var name = rawName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return UpdateRoleResult.InvalidName;
        }

        if (role.IsScopable && !GroupLimits.AllAllowed(permissions))
        {
            return UpdateRoleResult.NotAllowedWhenLimited;
        }

        var exists = await context.Roles.AnyAsync(r => r.Id != roleId && r.Name.ToLower() == name.ToLower(), cancellationToken).ConfigureAwait(false);
        if (exists)
        {
            return UpdateRoleResult.AlreadyExists;
        }

        var changes = new AuditChanges()
            .Field("Name", role.Name, name)
            .Set("Permissions", PermissionNames(role.Permissions), PermissionNames(permissions));
        if (!changes.Any)
        {
            return UpdateRoleResult.Updated;
        }

        AuditLog.Record(context, actor, AuditActions.RoleUpdated, AuditTarget.For(role), $"Updated role {name}", changes);
        role.Name = name;
        role.Permissions = permissions;

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return UpdateRoleResult.AlreadyExists;
        }

        return UpdateRoleResult.Updated;
    }

    /// <summary>Only while no one holds the role: turning the limit on would leave existing grants with no Groups,
    /// which means every domain, and turning it off would silently widen grants that were limited.</summary>
    public static async Task<SetLimitedToGroupsResult> SetLimitedToGroupsAsync(DotMarcDbContext context, AuditActor actor, int roleId, bool limitedToGroups, CancellationToken cancellationToken = default)
    {
        var role = await context.Roles.SingleAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role.IsLocked)
        {
            return SetLimitedToGroupsResult.Locked;
        }

        var changes = new AuditChanges().Field("Limited to groups", role.IsScopable, limitedToGroups);
        if (!changes.Any)
        {
            return SetLimitedToGroupsResult.Updated;
        }

        if (limitedToGroups && !GroupLimits.AllAllowed(role.Permissions))
        {
            return SetLimitedToGroupsResult.NotAllowedWhenLimited;
        }

        if (await IsInUseAsync(context, roleId, cancellationToken).ConfigureAwait(false))
        {
            return SetLimitedToGroupsResult.InUse;
        }

        role.IsScopable = limitedToGroups;
        AuditLog.Record(context, actor, AuditActions.RoleUpdated, AuditTarget.For(role), $"Updated role {role.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return SetLimitedToGroupsResult.Updated;
    }

    /// <summary>Unlike Group/Tag deletion (which only ever removes membership rows and is always
    /// safe), deleting a Role that's still granted to someone would leave their UserAccess row
    /// pointing at nothing - an undefined-permissions state. This checks first and refuses rather
    /// than letting that happen; the database's own DeleteBehavior.Restrict foreign key is a
    /// backstop behind this check, not the primary guard.</summary>
    public static async Task<RemoveRoleResult> RemoveRoleAsync(DotMarcDbContext context, AuditActor actor, int roleId, CancellationToken cancellationToken = default)
    {
        var role = await context.Roles.SingleAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false);
        if (role.IsLocked)
        {
            return RemoveRoleResult.Locked;
        }

        if (await IsInUseAsync(context, roleId, cancellationToken).ConfigureAwait(false))
        {
            return RemoveRoleResult.InUse;
        }

        // Saved with the delete, so a delete the database refuses below leaves no entry behind either.
        AuditLog.Record(context, actor, AuditActions.RoleRemoved, AuditTarget.For(role), $"Removed role {role.Name}");
        context.Roles.Remove(role);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23001" or "23503" })
        {
            // A UserAccess grant was inserted against this role in the window between the
            // AnyAsync check above and this SaveChangesAsync (READ COMMITTED, no explicit
            // locking) - the FK's DeleteBehavior.Restrict caught it. Report the same InUse
            // result the upfront check would have given, rather than letting the FK violation
            // leak out as an unhandled exception.
            //
            // SqlState is 23001 (restrict_violation), not 23503 (foreign_key_violation), because
            // this FK is configured with DeleteBehavior.Restrict, which Npgsql's migrations
            // generator emits as an explicit "ON DELETE RESTRICT" clause rather than the
            // clauseless default (ON DELETE NO ACTION) that raises 23503 - confirmed by actually
            // triggering this catch block against a real Postgres container
            // (RemoveRoleAsync_ReturnsInUse_WhenAGrantIsInsertedBetweenTheCheckAndTheDelete).
            // 23503 is kept alongside it as a defensive fallback in case that mapping ever
            // changes.
            return RemoveRoleResult.InUse;
        }

        return RemoveRoleResult.Removed;
    }

    private static async Task<bool> IsInUseAsync(DotMarcDbContext context, int roleId, CancellationToken cancellationToken) =>
        await context.UserAccesses.AnyAsync(u => u.RoleId == roleId, cancellationToken).ConfigureAwait(false)
        || await context.ApiKeys.AnyAsync(key => key.RoleId == roleId && key.RevokedUtc == null, cancellationToken).ConfigureAwait(false);

    private static IEnumerable<string> PermissionNames(IEnumerable<Permission> permissions) =>
        permissions.Select(permission => permission.ToString());
}

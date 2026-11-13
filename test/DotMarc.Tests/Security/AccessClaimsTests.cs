using DotMarc.Data;
using DotMarc.Security;
using Xunit;

namespace DotMarc.Tests.Security;

public sealed class AccessClaimsTests
{
    [Fact]
    public void AUser_GetsEveryPermissionAndTheirGroups()
    {
        var role = new Role { Name = "Viewer", IsScopable = true, Permissions = [Permission.DomainsView, Permission.DomainsEdit] };

        var claims = AccessClaims.For(role, [4, 9]).ToList();

        Assert.Equal(["DomainsView", "DomainsEdit"],
            claims.Where(claim => claim.Type == UserAccessClaimsTransformation.PermissionClaimType).Select(claim => claim.Value));
        Assert.Equal(["4", "9"],
            claims.Where(claim => claim.Type == UserAccessClaimsTransformation.ScopedGroupClaimType).Select(claim => claim.Value));
    }

    [Fact]
    public void AUserLimitedToGroups_NeverGetsATenantWidePermission()
    {
        // Granted before limited roles were checked, or held by an older Viewer: still dropped at sign-in.
        var role = new Role { Name = "Viewer", IsScopable = true, Permissions = [Permission.DomainsView, Permission.AccessManage, Permission.GroupsDelete] };

        var permissions = AccessClaims.For(role, [4])
            .Where(claim => claim.Type == UserAccessClaimsTransformation.PermissionClaimType)
            .Select(claim => claim.Value);

        Assert.Equal(["DomainsView"], permissions);
    }

    [Fact]
    public void AUserOfALimitableRoleGrantedWithNoGroups_KeepsEveryPermission()
    {
        // No Groups means unrestricted, so nothing reaches past a limit.
        var role = new Role { Name = "Viewer", IsScopable = true, Permissions = [Permission.DomainsView, Permission.AccessManage] };

        var permissions = AccessClaims.For(role, [])
            .Where(claim => claim.Type == UserAccessClaimsTransformation.PermissionClaimType)
            .Select(claim => claim.Value);

        Assert.Equal(["DomainsView", "AccessManage"], permissions);
    }

    [Fact]
    public void AnApiKey_NeverGetsAccessManage()
    {
        var role = new Role { Name = "Grew too much", Permissions = [Permission.DomainsView, Permission.AccessManage] };

        var permissions = AccessClaims.For(role, [], forApiKey: true)
            .Where(claim => claim.Type == UserAccessClaimsTransformation.PermissionClaimType)
            .Select(claim => claim.Value);

        Assert.Equal(["DomainsView"], permissions);
    }

    [Fact]
    public void GroupsAreIgnored_ForARoleThatIsntScopable()
    {
        var role = new Role { Name = "Editor", IsScopable = false, Permissions = [Permission.DomainsEdit] };

        var claims = AccessClaims.For(role, [4]);

        Assert.DoesNotContain(claims, claim => claim.Type == UserAccessClaimsTransformation.ScopedGroupClaimType);
    }
}

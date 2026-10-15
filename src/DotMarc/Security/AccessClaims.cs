using System.Globalization;
using System.Security.Claims;
using DotMarc.Data;

namespace DotMarc.Security;

/// <summary>The permission and scoped-group claims for a role and its groups, built the same way for a signed-in
/// person and an API key so the two can never be authorised differently. Groups count only on a scopable role. An API
/// key never gets AccessManage, even if its role gains it after the key was made. A grant that is actually limited to
/// Groups never gets a permission outside GroupLimits, even if its role holds one, since that permission would reach
/// past its Groups.</summary>
public static class AccessClaims
{
    public static IEnumerable<Claim> For(Role role, IEnumerable<int> scopedGroupIds, bool forApiKey = false)
    {
        var groupIds = role.IsScopable ? scopedGroupIds.ToList() : [];
        foreach (var permission in role.Permissions)
        {
            if (forApiKey && permission == Permission.AccessManage)
            {
                continue;
            }

            if (groupIds.Count > 0 && !GroupLimits.IsAllowed(permission))
            {
                continue;
            }

            yield return new Claim(UserAccessClaimsTransformation.PermissionClaimType, permission.ToString());
        }

        foreach (var groupId in groupIds)
        {
            yield return new Claim(UserAccessClaimsTransformation.ScopedGroupClaimType, groupId.ToString(CultureInfo.InvariantCulture));
        }
    }
}

using DotMarc.Psa.NinjaOne;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

public sealed class NinjaOneStateProtectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly NinjaOneStateProtector _protector = new(new EphemeralDataProtectionProvider());

    [Fact]
    public void TheStateIsValid_ForTheSameUser_WithinTenMinutes()
    {
        var state = _protector.Protect("user-1", Now);

        Assert.True(_protector.IsValid(state, "user-1", Now.AddMinutes(9)));
    }

    [Fact]
    public void AnotherUsersState_IsRefused() =>
        Assert.False(_protector.IsValid(_protector.Protect("user-1", Now), "user-2", Now));

    [Fact]
    public void AStateOlderThanTenMinutes_IsRefused() =>
        Assert.False(_protector.IsValid(_protector.Protect("user-1", Now), "user-1", Now.AddMinutes(11)));

    [Fact]
    public void ATamperedState_IsRefused() =>
        Assert.False(_protector.IsValid(_protector.Protect("user-1", Now) + "x", "user-1", Now));
}

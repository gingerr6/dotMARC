using DotMarc.Portal;
using MudBlazor;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class PortalHealthColoursTests
{
    [Theory]
    [InlineData(PortalHealth.Protected, Color.Success)]
    [InlineData(PortalHealth.NeedsAttention, Color.Warning)]
    [InlineData(PortalHealth.MonitoringOnly, Color.Secondary)]
    [InlineData(PortalHealth.NoReportsYet, Color.Secondary)]
    public void StatusesKeepTheirMeaning_AndTheNeutralOnesUseTheBrand(PortalHealth health, Color expected) =>
        Assert.Equal(expected, PortalHealthColours.For(health));
}

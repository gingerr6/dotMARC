using DotMarc.Data;
using DotMarc.Reporting;
using MudBlazor;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class MtaStsStatusPresentationTests
{
    [Theory]
    [InlineData(MtaStsStatus.Active, MtaStsMode.Enforce, "Active: enforcing", Color.Success)]
    [InlineData(MtaStsStatus.Active, MtaStsMode.Testing, "Active: testing", Color.Info)]
    [InlineData(MtaStsStatus.Active, MtaStsMode.None, "Active: withdrawn (none)", Color.Info)]
    [InlineData(MtaStsStatus.PendingDns, MtaStsMode.Enforce, "Waiting for DNS", Color.Info)]
    [InlineData(MtaStsStatus.Failed, MtaStsMode.Testing, "Failed", Color.Error)]
    public void ShowsTheModeOfAnActivePolicy(MtaStsStatus status, MtaStsMode mode, string label, Color color)
    {
        Assert.Equal(label, MtaStsStatusPresentation.GetLabel(status, mode));
        Assert.Equal(color, MtaStsStatusPresentation.GetColor(status, mode));
    }
}

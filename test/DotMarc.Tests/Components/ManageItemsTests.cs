using DotMarc.Components.Layout;
using Xunit;

namespace DotMarc.Tests.Components;

public sealed class ManageItemsTests
{
    [Theory]
    [InlineData("/domains", "/manage")]
    [InlineData("/control-panel/updates", "/manage")]
    [InlineData("/backups", "/manage")]
    [InlineData("/groups/12/setup", "/groups")]
    [InlineData("/domains/import", "/domains")]
    public void Back_goes_to_the_overview_or_the_sub_pages_own_page(string path, string expected) =>
        Assert.Equal(expected, ManageItems.ParentOf(path)?.Href);

    [Theory]
    [InlineData("/manage")]
    [InlineData("/manage/")]
    public void The_overview_has_no_back_button(string path) => Assert.Null(ManageItems.ParentOf(path));
}

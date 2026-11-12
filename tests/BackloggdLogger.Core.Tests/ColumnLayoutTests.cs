using BackloggdLogger.Core;

namespace BackloggdLogger.Core.Tests;

public class ColumnLayoutTests
{
    // Icon, Name, Time played, Added, Backloggd, Source: the Tracked games list with Status hidden.
    static readonly ColumnSpec Icon = ColumnSpec.Fixed(32);
    static readonly ColumnSpec Time = ColumnSpec.Fixed(64);
    static readonly ColumnSpec Added = ColumnSpec.Fixed(92);
    static readonly ColumnSpec Name = ColumnSpec.Flex(weight: 5, min: 160, floor: 64, shrinkOrder: 3);
    static readonly ColumnSpec Backloggd = ColumnSpec.Flex(weight: 3, min: 120, floor: 48, shrinkOrder: 2);
    static readonly ColumnSpec Source = ColumnSpec.Flex(weight: 3, min: 100, floor: 48, shrinkOrder: 1);
    static readonly ColumnSpec[] Tracked = { Icon, Name, Time, Added, Backloggd, Source };

    [Theory]
    [InlineData(400)]
    [InlineData(567)]
    [InlineData(568)]
    [InlineData(569)]
    [InlineData(700)]
    [InlineData(853)]
    [InlineData(1056)]
    [InlineData(1073)]
    [InlineData(1600)]
    [InlineData(3840)]
    public void The_columns_fill_the_width_exactly(int width)
    {
        var w = ColumnLayout.Fit(width, Tracked);
        Assert.Equal(width, w.Sum());
        Assert.Equal(new[] { 32, 64, 92 }, new[] { w[0], w[2], w[3] });
    }

    [Fact]
    public void Every_width_from_the_floors_up_is_filled_exactly()
    {
        var least = 32 + 64 + 92 + 64 + 48 + 48;
        for (var width = least; width < 2000; width++)
            Assert.Equal(width, ColumnLayout.Fit(width, Tracked).Sum());
    }

    [Fact]
    public void Flexible_columns_share_by_weight_and_Name_gets_the_most()
    {
        var w = ColumnLayout.Fit(32 + 64 + 92 + 1100, Tracked);
        Assert.Equal(new[] { 32, 500, 64, 92, 300, 300 }, w);
    }

    [Fact]
    public void A_remainder_goes_to_the_heaviest_column()
    {
        var w = ColumnLayout.Fit(32 + 64 + 92 + 1105, Tracked);
        Assert.Equal(1105, w[1] + w[4] + w[5]);
        Assert.Equal(301, w[4]);
        Assert.Equal(301, w[5]);
        Assert.Equal(503, w[1]);
    }

    [Fact]
    public void A_share_under_its_minimum_is_held_at_the_minimum_and_the_rest_share_what_is_left()
    {
        // 400 to share: by weight Backloggd would get 109, under its 120.
        var w = ColumnLayout.Fit(32 + 64 + 92 + 400, Tracked);
        Assert.Equal(120, w[4]);
        Assert.True(w[5] >= 100);
        Assert.True(w[1] >= 160);
        Assert.Equal(400, w[1] + w[4] + w[5]);
    }

    [Fact]
    public void At_the_minimums_every_flexible_column_is_its_minimum()
    {
        var w = ColumnLayout.Fit(32 + 64 + 92 + 380, Tracked);
        Assert.Equal(new[] { 32, 160, 64, 92, 120, 100 }, w);
    }

    [Fact]
    public void Too_narrow_for_the_minimums_shrinks_Source_first_then_Backloggd_then_Name()
    {
        var fixedSum = 32 + 64 + 92;
        Assert.Equal(new[] { 32, 160, 64, 92, 120, 70 }, ColumnLayout.Fit(fixedSum + 350, Tracked));
        Assert.Equal(new[] { 32, 160, 64, 92, 120, 48 }, ColumnLayout.Fit(fixedSum + 328, Tracked));
        Assert.Equal(new[] { 32, 160, 64, 92, 100, 48 }, ColumnLayout.Fit(fixedSum + 308, Tracked));
        Assert.Equal(new[] { 32, 160, 64, 92, 48, 48 }, ColumnLayout.Fit(fixedSum + 256, Tracked));
        Assert.Equal(new[] { 32, 100, 64, 92, 48, 48 }, ColumnLayout.Fit(fixedSum + 196, Tracked));
    }

    [Fact]
    public void Fixed_columns_are_never_cut_even_under_the_floors()
    {
        var w = ColumnLayout.Fit(100, Tracked);
        Assert.Equal(new[] { 32, 64, 64, 92, 48, 48 }, w);
    }

    [Fact]
    public void Hidden_columns_leave_their_width_to_the_others()
    {
        // Source hidden.
        var w = ColumnLayout.Fit(988, new[] { Icon, Name, Time, Added, Backloggd });
        Assert.Equal(988, w.Sum());
        Assert.Equal(new[] { 32, 500, 64, 92, 300 }, w);
        // Only the icon and Name shown.
        Assert.Equal(new[] { 32, 668 }, ColumnLayout.Fit(700, new[] { Icon, Name }));
    }

    [Fact]
    public void With_no_flexible_column_the_last_one_takes_the_rest()
    {
        Assert.Equal(new[] { 32, 64, 204 }, ColumnLayout.Fit(300, new[] { Icon, Time, Added }));
        Assert.Empty(ColumnLayout.Fit(300, Array.Empty<ColumnSpec>()));
    }
}

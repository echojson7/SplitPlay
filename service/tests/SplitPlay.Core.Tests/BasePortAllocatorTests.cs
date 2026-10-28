// SPDX-License-Identifier: MIT
using SplitPlay.Core.Ports;

namespace SplitPlay.Core.Tests;

public sealed class BasePortAllocatorTests
{
    [Fact]
    public void First_desk_gets_the_start_port()
    {
        Assert.Equal(48089, BasePortAllocator.Allocate(48089, 100, []));
    }

    [Fact]
    public void Lowest_free_slot_is_chosen()
    {
        Assert.Equal(48189, BasePortAllocator.Allocate(48089, 100, [48089, 48289]));
        Assert.Equal(48389, BasePortAllocator.Allocate(48089, 100, [48089, 48189, 48289]));
    }

    [Fact]
    public void Slots_overlapping_a_manual_port_are_skipped()
    {
        // A manual desk at 48200 occupies 48195-48221, which overlaps the 48189 slot (48184-48210).
        Assert.Equal(48289, BasePortAllocator.Allocate(48089, 100, [48089, 48200]));
    }

    [Fact]
    public void Reserved_ports_are_avoided()
    {
        Assert.Equal(38389, BasePortAllocator.Allocate(38289, 100, [], [38299]));
    }

    [Fact]
    public void Exhaustion_throws()
    {
        Assert.Throws<InvalidOperationException>(() => BasePortAllocator.Allocate(65500, 100, [65500]));
    }

    [Fact]
    public void Invalid_arguments_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BasePortAllocator.Allocate(3, 100, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => BasePortAllocator.Allocate(48089, 0, []));
    }

    [Fact]
    public void Range_spans_base_minus_5_to_base_plus_21()
    {
        var range = BasePortAllocator.RangeOf(48089);
        Assert.Equal(new PortRange(48084, 48110), range);
        Assert.Equal(27, BasePortAllocator.PortSpan);
        Assert.True(range.Contains(48084));
        Assert.True(range.Contains(48110));
        Assert.False(range.Contains(48111));
        Assert.True(range.Overlaps(new PortRange(48110, 48200)));
        Assert.False(range.Overlaps(new PortRange(48111, 48200)));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    [InlineData(65514, true)]
    [InlineData(65515, false)]
    public void Base_port_bounds(int port, bool valid)
    {
        Assert.Equal(valid, BasePortAllocator.IsValidBasePort(port));
    }
}

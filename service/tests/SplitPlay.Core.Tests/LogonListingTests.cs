// SPDX-License-Identifier: MIT
using SplitPlay.Core.Host;
using SplitPlay.Core.Platform;
using SplitPlay.Core.Steam;

namespace SplitPlay.Core.Tests;

public sealed class LogonListingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "splitplay-logon-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly MemoryUserHive _hklm = new();

    private LogonListing Listing() => new(Path.Combine(_dir, "desk-1", LogonListing.StateFileName));

    private RegValue? Value(string user) => _hklm.Get(LogonListing.UserListKey, user);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Hide_then_show_leaves_no_trace()
    {
        var listing = Listing();
        Assert.True(listing.Hide(_hklm, "sp-desk-1"));
        Assert.Equal("0", Value("sp-desk-1")?.Data);
        Assert.Equal("sp-desk-1", listing.RecordedUser());
        Assert.False(listing.Hide(_hklm, "sp-desk-1"));

        Assert.True(listing.Show(_hklm));
        Assert.Null(Value("sp-desk-1"));
        Assert.Null(listing.RecordedUser());
        Assert.False(File.Exists(listing.StateFile));
        Assert.False(listing.Show(_hklm));
    }

    [Fact]
    public void An_owner_value_from_before_comes_back_exactly()
    {
        _hklm.Set(LogonListing.UserListKey, "gamer", RegValue.DWord(1));
        _hklm.Set(LogonListing.UserListKey, "hidden-already", RegValue.DWord(0));
        var listing = Listing();
        listing.Hide(_hklm, "gamer");
        Assert.Equal("0", Value("gamer")?.Data);
        listing.Show(_hklm);
        Assert.Equal("1", Value("gamer")?.Data);

        // Already hidden by the owner: showing again keeps it hidden.
        Assert.False(listing.Hide(_hklm, "hidden-already"));
        Assert.False(listing.Show(_hklm));
        Assert.Equal("0", Value("hidden-already")?.Data);
    }

    [Fact]
    public void A_new_user_shows_the_old_one_again_and_a_changed_value_is_left_alone()
    {
        var listing = Listing();
        listing.Hide(_hklm, "old");
        listing.Hide(_hklm, "new");
        Assert.Null(Value("old"));
        Assert.Equal("0", Value("new")?.Data);

        // Someone made the account visible by hand since: Show does not undo their change.
        _hklm.Set(LogonListing.UserListKey, "new", RegValue.DWord(1));
        Assert.False(listing.Show(_hklm));
        Assert.Equal("1", Value("new")?.Data);
    }
}

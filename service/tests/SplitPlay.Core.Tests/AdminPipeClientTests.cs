// SPDX-License-Identifier: MIT
using Microsoft.Extensions.Time.Testing;
using SplitPlay.Core.Ipc;

namespace SplitPlay.Core.Tests;

/// <summary>The admin pipe's clients: the CLI's token cache and the tray's sign-in reloads.</summary>
public sealed class AdminPipeClientTests
{
    private static byte[] Flip(byte[] data) => [.. data.Select(b => (byte)~b)];

    [Fact]
    public void Token_cache_keeps_a_token_while_enough_of_its_lifetime_is_left()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sp-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
            var path = Path.Combine(dir, "cli-token-s1.bin");
            var cache = new AdminTokenCache(path, Flip, Flip, time);
            Assert.Null(cache.Read());

            cache.Write(new AdminToken("tok-1", null, time.GetUtcNow().AddMinutes(15)));
            Assert.Equal("tok-1", cache.Read());
            Assert.DoesNotContain("tok-1", File.ReadAllText(path), StringComparison.Ordinal); // stored protected

            time.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(1));
            Assert.Null(cache.Read()); // less than MinRemaining left

            cache.Write(new AdminToken("tok-2", null, time.GetUtcNow().AddMinutes(15)));
            cache.Clear();
            Assert.Null(cache.Read());

            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Null(cache.Read()); // unreadable
            Assert.Null(new AdminTokenCache(path, Flip, _ => throw new System.Security.Cryptography.CryptographicException(), time).Read());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Token_cache_secures_the_file_before_it_takes_the_cache_name_or_caches_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sp-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
            var path = Path.Combine(dir, "cli-token-s1.bin");
            var secured = new List<string>();
            var cache = new AdminTokenCache(path, Flip, Flip, time, f =>
            {
                Assert.False(File.Exists(path)); // labelled while it still has its temporary name
                secured.Add(f);
            });
            cache.Write(new AdminToken("tok-1", null, time.GetUtcNow().AddMinutes(15)));
            Assert.Equal([path + ".tmp"], secured);
            Assert.Equal("tok-1", cache.Read());

            // When the file cannot be secured, nothing is cached and the older token is gone too.
            var failing = new AdminTokenCache(path, Flip, Flip, time, _ => throw new System.ComponentModel.Win32Exception(5));
            failing.Write(new AdminToken("tok-2", null, time.GetUtcNow().AddMinutes(15)));
            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Null(failing.Read());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Tray_stops_reloading_when_the_api_keeps_rejecting_the_pipe_session()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var gate = new TraySignInGate(time);

        // The page asks; the tray signs in and reloads; the page asks again soon after: a failed reload.
        var reloads = 0;
        for (var i = 0; i < 20; i++)
        {
            if (gate.ShouldSignIn())
            {
                gate.Reloaded();
                reloads++;
            }

            time.Advance(TimeSpan.FromSeconds(11));
        }

        Assert.Equal(TraySignInGate.MaxFailedReloads, reloads);
        Assert.True(gate.Stopped);
        time.Advance(TimeSpan.FromHours(1));
        Assert.False(gate.ShouldSignIn()); // stays on the password login until the dashboard is opened again

        gate.Reset();
        Assert.True(gate.ShouldSignIn());
        Assert.False(gate.ShouldSignIn()); // at most every MinInterval
    }

    [Fact]
    public void Tray_signs_in_again_when_a_session_expires_long_after_a_good_reload()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var gate = new TraySignInGate(time);
        for (var i = 0; i < 10; i++)
        {
            Assert.True(gate.ShouldSignIn());
            gate.Reloaded();
            time.Advance(TraySignInGate.FailureWindow + TimeSpan.FromSeconds(1));
        }

        Assert.False(gate.Stopped);
    }
}

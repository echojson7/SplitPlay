// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using SplitPlay.Core.Host;

namespace SplitPlay.Core.Platform;

/// <summary>
/// Keeps a desk's Windows account off the sign-in screen: the DWORD <c>&lt;user&gt; = 0</c> under
/// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList</c>. The account still works
/// for RDP (desk sessions) and in <c>Other user</c>. A state file in the desk directory records the user and the value
/// that was there before, so that <see cref="Show"/> puts back exactly that and nothing else.
/// </summary>
/// <remarks>
/// Applied by default only to accounts SplitPlay created. An account SplitPlay did not create is hidden only when the
/// owner turns it on for that desk (<c>DeskConfig.HideFromLogon</c>).
/// </remarks>
public sealed class LogonListing(string stateFile)
{
    public const string StateFileName = "logon-hidden.json";

    public const string UserListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList";

    private static readonly RegValue Hidden = RegValue.DWord(0);

    /// <summary>What was recorded: the hidden user and the value before (null: there was none).</summary>
    public sealed record State(string User, RegValue? Previous);

    public string StateFile { get; } = stateFile;

    /// <summary>The user this desk hid, or null.</summary>
    public string? RecordedUser() => Read()?.User;

    /// <summary>
    /// Hides <paramref name="user"/>. A different user hidden earlier for this desk is shown again first. Returns true
    /// if the registry was changed.
    /// </summary>
    public bool Hide(IHostRegistry hklm, string user)
    {
        ArgumentNullException.ThrowIfNull(hklm);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        var state = Read();
        if (state is not null && !string.Equals(state.User, user, StringComparison.OrdinalIgnoreCase))
        {
            Show(hklm);
            state = null;
        }

        var current = hklm.Get(UserListKey, user);
        if (state is null)
        {
            // Recorded before the change: a crash in between leaves a record that Show can still undo.
            Write(new State(user, current));
        }

        if (Hidden.SameAs(current))
        {
            return false;
        }

        hklm.Set(UserListKey, user, Hidden);
        return true;
    }

    /// <summary>
    /// Undoes <see cref="Hide"/>: the value before comes back (removed when there was none). A value someone changed
    /// since is left as it is. Returns true if the registry was changed; the record is removed either way.
    /// </summary>
    public bool Show(IHostRegistry hklm)
    {
        ArgumentNullException.ThrowIfNull(hklm);
        var state = Read();
        if (state is null)
        {
            return false;
        }

        var changed = false;
        if (Hidden.SameAs(hklm.Get(UserListKey, state.User)) && !Hidden.SameAs(state.Previous))
        {
            if (state.Previous is null)
            {
                hklm.Delete(UserListKey, state.User);
            }
            else
            {
                hklm.Set(UserListKey, state.User, state.Previous);
            }

            changed = true;
        }

        Forget();
        return changed;
    }

    /// <summary>Removes the record without touching the registry.</summary>
    public void Forget()
    {
        try
        {
            File.Delete(StateFile);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    public State? Read()
    {
        string text;
        try
        {
            text = File.ReadAllText(StateFile);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("user", out var u) || u.GetString() is not { Length: > 0 } user)
            {
                return null;
            }

            RegValue? previous = null;
            if (root.TryGetProperty("previous", out var p) && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("kind", out var k) && Enum.TryParse<RegKind>(k.GetString(), out var kind)
                && p.TryGetProperty("data", out var d) && d.GetString() is { } data)
            {
                previous = new RegValue(kind, data);
            }

            return new State(user, previous);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Write(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(StateFile))!);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("user", state.User);
            if (state.Previous is { } previous)
            {
                json.WriteStartObject("previous");
                json.WriteString("kind", previous.Kind.ToString());
                json.WriteString("data", previous.Data);
                json.WriteEndObject();
            }
            else
            {
                json.WriteNull("previous");
            }

            json.WriteEndObject();
        }

        var temp = StateFile + ".tmp";
        File.WriteAllText(temp, Encoding.UTF8.GetString(stream.ToArray()));
        File.Move(temp, StateFile, overwrite: true);
    }
}

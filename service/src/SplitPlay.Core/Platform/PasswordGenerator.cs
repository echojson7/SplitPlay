// SPDX-License-Identifier: MIT
using System.Security.Cryptography;

namespace SplitPlay.Core.Platform;

/// <summary>Random passwords for desk accounts: 32 characters with upper, lower, digit and special characters.</summary>
public static class PasswordGenerator
{
    public const int DefaultLength = 32;

    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";

    // Special characters include the ones Duo had trouble with (quotes, backslash, percent); the password only
    // travels as JSON over the seat pipe and through FreeRDP's API, never through a shell or a command line.
    private const string Specials = "!#$%&()*+,-./:;<=>?@[]^_{|}~\"'\\";

    private static readonly string All = Upper + Lower + Digits + Specials;

    public static string Generate(int length = DefaultLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 8);
        var chars = new char[length];
        // One of each class (Windows password complexity), the rest from the full set, then shuffled.
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        chars[3] = Pick(Specials);
        for (var i = 4; i < length; i++)
        {
            chars[i] = Pick(All);
        }

        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
}

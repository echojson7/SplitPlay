// SPDX-License-Identifier: MIT
using System.Globalization;

namespace SplitPlay.Cli;

/// <summary>Bad command line; printed with the usage text, exit code 2.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>
/// Minimal argument reader: positionals plus <c>--name value</c>, <c>--name=value</c> and boolean <c>--flag</c> options.
/// </summary>
internal sealed class Args
{
    private readonly List<string> _positionals = [];
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public Args(IEnumerable<string> args, IReadOnlySet<string> flags)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (arg == "--")
            {
                _positionals.AddRange(list.Skip(i + 1));
                break;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var eq = arg.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    _options[arg[2..eq]] = arg[(eq + 1)..];
                }
                else if (flags.Contains(arg[2..]))
                {
                    _options[arg[2..]] = null;
                }
                else if (i + 1 < list.Count)
                {
                    _options[arg[2..]] = list[++i];
                }
                else
                {
                    throw new UsageException($"{arg} needs a value");
                }
            }
            else
            {
                _positionals.Add(arg);
            }
        }
    }

    public IReadOnlyList<string> Positionals => _positionals;

    public string Positional(int index, string name) =>
        index < _positionals.Count ? _positionals[index] : throw new UsageException($"missing <{name}>");

    public bool Flag(string name)
    {
        _used.Add(name);
        return _options.ContainsKey(name);
    }

    public string? Option(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out var value) ? value : null;
    }

    public int? IntOption(string name)
    {
        var value = Option(name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new UsageException($"--{name} must be an integer");
    }

    /// <summary>Fails on options nobody asked for (typos).</summary>
    public void EnsureNoUnknownOptions()
    {
        var unknown = _options.Keys.Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            throw new UsageException($"unknown option(s): {string.Join(", ", unknown.Select(u => "--" + u))}");
        }
    }
}

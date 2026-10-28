// SPDX-License-Identifier: MIT
using System.Text;

namespace SplitPlay.Core.Platform;

/// <summary>
/// Builds a Windows command line that <c>CommandLineToArgvW</c> and the MSVC/MinGW CRT split back into exactly the
/// given arguments (same rules as .NET's <c>ProcessStartInfo.ArgumentList</c>).
/// </summary>
public static class WindowsCommandLine
{
    public static string Build(string executable, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var sb = new StringBuilder();
        // The program name is parsed differently (no backslash escaping); quotes inside a path are impossible anyway.
        sb.Append('"').Append(executable.Replace("\"", "", StringComparison.Ordinal)).Append('"');
        foreach (var argument in arguments)
        {
            sb.Append(' ');
            AppendArgument(sb, argument);
        }

        return sb.ToString();
    }

    public static void AppendArgument(StringBuilder sb, string argument)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            sb.Append(argument);
            return;
        }

        sb.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // Backslashes before a quote are doubled, and the quote itself is escaped.
                sb.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                sb.Append('\\', backslashes);
            }

            backslashes = 0;
            sb.Append(c);
        }

        // Backslashes before the closing quote are doubled.
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    /// <summary>
    /// Splits a command line the way <c>CommandLineToArgvW</c> does for the arguments after the program name
    /// (used by tests to check <see cref="Build"/>).
    /// </summary>
    public static IReadOnlyList<string> Parse(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var args = new List<string>();
        var i = 0;

        // Program name: up to the next unquoted whitespace, quotes toggle, no escapes.
        var inQuotes = false;
        var program = new StringBuilder();
        for (; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && (c == ' ' || c == '\t'))
            {
                break;
            }
            else
            {
                program.Append(c);
            }
        }

        args.Add(program.ToString());
        while (true)
        {
            while (i < commandLine.Length && (commandLine[i] == ' ' || commandLine[i] == '\t'))
            {
                i++;
            }

            if (i >= commandLine.Length)
            {
                break;
            }

            var arg = new StringBuilder();
            inQuotes = false;
            while (i < commandLine.Length && (inQuotes || (commandLine[i] != ' ' && commandLine[i] != '\t')))
            {
                var backslashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    arg.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1)
                    {
                        arg.Append('"');
                    }
                    else if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                    {
                        arg.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }

                    i++;
                }
                else
                {
                    arg.Append('\\', backslashes);
                    if (i < commandLine.Length && (inQuotes || (commandLine[i] != ' ' && commandLine[i] != '\t')))
                    {
                        arg.Append(commandLine[i]);
                        i++;
                    }
                }
            }

            args.Add(arg.ToString());
        }

        return args;
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Piper.Runtime;

/// <summary>
/// A program to run and its arguments, as <c>shell$"git log {branch} --oneline"</c> makes it. The literal's text is split
/// into arguments at spaces (quotes in it group words: <c>"two words"</c>), and each hole is one argument, whatever it
/// holds, or part of the one it's written against (<c>--author={name}</c>). No shell reads it: the program is started
/// with its arguments as a list, so a hole can't add an argument, a pipe or a second command.
/// </summary>
[InterpolationKind("shell", Holds = "text, a number, a bool, a date or time, a Guid, or another formattable value (one argument), " +
                                     "an Option of one (None leaves its argument out), a list of them (an argument each), or another Shell")]
public sealed class Shell : IEquatable<Shell>
{
    private readonly string[] _words;

    internal Shell(string[] words) => _words = words;

    /// <summary>What <c>shell$"…"</c> compiles to.</summary>
    public static ShellBuilder Builder() => new();

    /// <summary>A program and its arguments, as they are.</summary>
    public static Shell Of(string program, params string[] arguments) => new([program, .. arguments]);

    /// <summary>The program: the first word.</summary>
    public string Program => _words.Length > 0 ? _words[0] : "";

    /// <summary>The arguments after the program, each as the program receives it.</summary>
    public IReadOnlyList<string> Arguments => _words.Length > 1 ? _words[1..] : [];

    internal string[] Words => _words;

    /// <summary>How to start it: the program, with <see cref="Arguments"/> as its argument list, and its output read.</summary>
    public ProcessStartInfo ToStartInfo()
    {
        if (_words.Length == 0)
            throw new InvalidOperationException("The command is empty: there's no program to run.");
        var info = new ProcessStartInfo(Program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in Arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    /// <summary>Runs it to the end, giving its exit code and what it wrote.</summary>
    public async Task<ShellResult> RunAsync(CancellationToken cancellation = default)
    {
        using var process = Process.Start(ToStartInfo()) ?? throw new InvalidOperationException($"'{Program}' couldn't be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellation);
        var error = process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);
        return new ShellResult(process.ExitCode, await output, await error);
    }

    /// <summary>Runs it to the end, giving its exit code and what it wrote.</summary>
    public ShellResult Run() => RunAsync().GetAwaiter().GetResult();

    /// <summary>The command as it might be typed, each word with spaces or quotes in it quoted: for showing, not running.</summary>
    public override string ToString() => string.Join(" ", _words.Select(Quote));

    private static string Quote(string word) =>
        word.Length > 0 && !word.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? word : "\"" + word.Replace("\"", "\\\"") + "\"";

    public bool Equals(Shell? other) => other is not null && _words.AsSpan().SequenceEqual(other._words);

    public override bool Equals(object? obj) => obj is Shell other && Equals(other);

    public override int GetHashCode() => ToString().GetHashCode();

    public static bool operator ==(Shell? a, Shell? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(Shell? a, Shell? b) => !(a == b);
}

/// <summary>What a program that ran wrote, and how it ended.</summary>
public sealed record ShellResult(int exitCode, string output, string error)
{
    /// <summary>Whether it ended with exit code 0.</summary>
    public bool Succeeded => exitCode == 0;
}

/// <summary>
/// Builds a <see cref="Shell"/>: what <c>shell$"…"</c> compiles to. Text is split into words at spaces outside quotes;
/// a hole is added to the word it's written in, or is a word of its own, even if it's empty or has spaces in it. A list
/// on its own is a word for each item, and a Shell on its own is its words; written against text, each is joined
/// into that word with spaces. None leaves out the word it's in.
/// </summary>
public sealed class ShellBuilder
{
    private readonly List<string> _words = [];
    private StringBuilder? _word;
    private bool _dropped;
    private char _quote;

    /// <summary>The literal's own text.</summary>
    public ShellBuilder Text(string text)
    {
        foreach (char c in text)
        {
            if (_quote != 0)
            {
                if (c == _quote)
                    _quote = '\0';
                else
                    Word().Append(c);
            }
            else if (c is '"' or '\'')
            {
                _quote = c;
                Word();
            }
            else if (char.IsWhiteSpace(c))
                EndWord();
            else
                Word().Append(c);
        }
        return this;
    }

    public ShellBuilder Hole(string value)
    {
        Word().Append(value);
        return this;
    }

    public ShellBuilder Hole(Shell shell) => Several(shell.Words);

    public ShellBuilder Hole(bool value) => Hole(value ? "true" : "false");

    public ShellBuilder Hole(char value) => Hole(value.ToString());

    public ShellBuilder Hole(DateTime value) => Hole(value.ToString("O", CultureInfo.InvariantCulture));

    public ShellBuilder Hole(DateTimeOffset value) => Hole(value.ToString("O", CultureInfo.InvariantCulture));

    public ShellBuilder Hole(DateOnly value) => Hole(value.ToString("O", CultureInfo.InvariantCulture));

    public ShellBuilder Hole(TimeOnly value) => Hole(value.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>A number, Guid or other formattable value, in the invariant culture.</summary>
    public ShellBuilder Hole<T>(T value) where T : IFormattable => Hole(value.ToString(null, CultureInfo.InvariantCulture));

    /// <summary><c>{size:F1}</c>: formatted, in the invariant culture.</summary>
    public ShellBuilder Hole<T>(T value, string format) where T : IFormattable => Hole(value.ToString(format, CultureInfo.InvariantCulture));

    public ShellBuilder Hole(Option<string> value) => value.IsSome ? Hole(value.Value) : Drop();
    public ShellBuilder Hole(Option<bool> value) => value.IsSome ? Hole(value.Value) : Drop();
    public ShellBuilder Hole(Option<DateTime> value) => value.IsSome ? Hole(value.Value) : Drop();
    public ShellBuilder Hole(Option<DateOnly> value) => value.IsSome ? Hole(value.Value) : Drop();

    /// <summary>An optional number, Guid, newtype or other formattable value.</summary>
    public ShellBuilder Hole<T>(Option<T> value) where T : IFormattable => value.IsSome ? Hole(value.Value) : Drop();

    public ShellBuilder Hole(IEnumerable<string> values) => Several([.. values]);

    public ShellBuilder Hole<T>(IEnumerable<T> values) where T : IFormattable =>
        Several([.. values.Select(v => v.ToString(null, CultureInfo.InvariantCulture))]);

    /// <exception cref="FormatException">A quote in the literal isn't closed.</exception>
    public Shell Build()
    {
        if (_quote != 0)
            throw new FormatException($"A {_quote} in the command isn't closed.");
        EndWord();
        return new([.. _words]);
    }

    private StringBuilder Word() => _word ??= new StringBuilder();

    /// <summary>On its own, a word each; written against text (or in quotes), joined into that word with spaces.</summary>
    private ShellBuilder Several(IReadOnlyList<string> words)
    {
        if (_word is null && _quote == 0)
            _words.AddRange(words);
        else
            Word().Append(string.Join(" ", words));
        return this;
    }

    /// <summary>None: the word it's in is left out.</summary>
    private ShellBuilder Drop()
    {
        Word();
        _dropped = true;
        return this;
    }

    private void EndWord()
    {
        if (_word is not null && !_dropped)
            _words.Add(_word.ToString());
        _word = null;
        _dropped = false;
    }
}

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace Piper.Runtime;

/// <summary>
/// One option an <c>args</c> declaration (the args plugin) accepts: <c>--port &lt;int&gt;</c>.
/// </summary>
/// <param name="Name">As written on the command line, without the dashes: <c>dry-run</c>.</param>
/// <param name="Type">bool (a flag), string, int, long, double or decimal.</param>
/// <param name="Required">Whether the program can't run without it (it has no default and isn't optional).</param>
/// <param name="Default">The default, as shown in the usage.</param>
public sealed record PiperOption(string Name, string Type, string? Description, bool Required, string? Default)
{
    public bool IsFlag => Type == "bool";
}

/// <summary>
/// A command line read against the options an <c>args</c> declaration accepts: <c>--name value</c>, <c>--name=value</c>,
/// <c>--flag</c>, <c>-h</c>/<c>--help</c>, and other arguments in order (all of them after <c>--</c>). The first
/// mistake (an unknown option, a missing or malformed value, a missing required option) is <see cref="Error"/>.
/// </summary>
public sealed class PiperArguments
{
    private static readonly Dictionary<string, Func<string, bool>> Parsers = new()
    {
        ["string"] = _ => true,
        ["int"] = text => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        ["long"] = text => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        ["double"] = text => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        ["decimal"] = text => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
    };

    private readonly Dictionary<string, string> _values = [];
    private readonly HashSet<string> _flags = [];
    private readonly List<string> _positionals = [];

    private PiperArguments(string usage) => Usage = usage;

    /// <summary>What went wrong, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether <c>-h</c> or <c>--help</c> was given (and nothing else was then checked).</summary>
    public bool Help { get; private set; }

    /// <summary>The program's usage: its options, their types, descriptions and defaults.</summary>
    public string Usage { get; }

    /// <param name="positionals">What the other arguments are called (<c>files</c>), or null if the program takes none.</param>
    /// <remarks>The program's name in the usage is its assembly's, the one calling this.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static PiperArguments Read(string[] args, string? description, IReadOnlyList<PiperOption> options, string? positionals)
    {
        var program = Assembly.GetCallingAssembly().GetName().Name ?? "program";
        var arguments = new PiperArguments(MakeUsage(program, description, options, positionals));
        arguments.Parse(args, options, positionals);
        return arguments;
    }

    /// <summary>Whether <paramref name="name"/>, a flag, was given.</summary>
    public bool Flag(string name) => _flags.Contains(name);

    /// <summary>The text given for <paramref name="name"/>, or null.</summary>
    public string? Text(string name) => _values.GetValueOrDefault(name);

    /// <summary>The number given for <paramref name="name"/> (already checked when read), or null.</summary>
    public T? Value<T>(string name) where T : struct, IParsable<T> =>
        _values.TryGetValue(name, out var text) ? T.Parse(text, CultureInfo.InvariantCulture) : null;

    /// <summary>The arguments that aren't options, in order.</summary>
    public List<string> Positionals() => [.. _positionals];

    /// <summary>
    /// For <c>FromArgs</c>: prints <paramref name="message"/> (the usage, after any error) and ends the program, with 0
    /// when help was asked for and 2 for a mistake, as command-line tools do.
    /// </summary>
    public static T Exit<T>(string[] args, string message)
    {
        if (IsHelp(args))
        {
            Console.Out.WriteLine(message);
            Environment.Exit(0);
        }
        Console.Error.WriteLine(message);
        Environment.Exit(2);
        throw new InvalidOperationException("Unreachable: the program has exited.");
    }

    private static bool IsHelp(string[] args) => args.TakeWhile(a => a != "--").Any(a => a is "-h" or "--help");

    private void Parse(string[] args, IReadOnlyList<PiperOption> options, string? positionals)
    {
        if (IsHelp(args))
        {
            Help = true;
            return;
        }

        var byName = options.ToDictionary(o => o.Name);
        for (int i = 0; i < args.Length && Error is null; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                foreach (var rest in args.Skip(i + 1))
                    AddPositional(rest, positionals);
                break;
            }
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                if (arg.StartsWith('-') && arg.Length > 1 && !double.TryParse(arg, CultureInfo.InvariantCulture, out _))
                    Error = $"Unknown option '{arg}'; options are written '--name'.";
                else
                    AddPositional(arg, positionals);
                continue;
            }

            var (name, inline) = arg.IndexOf('=') is var at and > 0 ? (arg[2..at], arg[(at + 1)..]) : (arg[2..], null);
            if (!byName.TryGetValue(name, out var option))
            {
                var near = options.Select(o => o.Name).FirstOrDefault(o => Distance(o, name) <= 2);
                Error = $"Unknown option '--{name}'{(near is null ? "" : $"; did you mean '--{near}'?")}";
                continue;
            }
            if (option.IsFlag)
            {
                if (inline is not null)
                    Error = $"'--{name}' is a flag, so it takes no value.";
                _flags.Add(name);
                continue;
            }

            var value = inline;
            if (value is null)
            {
                if (i + 1 >= args.Length || (args[i + 1].StartsWith("--", StringComparison.Ordinal) && args[i + 1].Length > 2))
                {
                    Error = $"'--{name}' needs a value: --{name} <{option.Type}>.";
                    continue;
                }
                value = args[++i];
            }
            if (!Parsers[option.Type](value))
                Error = $"'--{name}' must be {Article(option.Type)} {option.Type}, not '{value}'.";
            else
                _values[name] = value;
        }

        if (Error is null && options.FirstOrDefault(o => o.Required && !_values.ContainsKey(o.Name)) is { } missing)
            Error = $"'--{missing.Name}' is required.";
    }

    private void AddPositional(string arg, string? positionals)
    {
        if (positionals is null)
            Error ??= $"Unexpected argument '{arg}'; this program takes only options.";
        else
            _positionals.Add(arg);
    }

    private static string MakeUsage(string program, string? description, IReadOnlyList<PiperOption> options, string? positionals)
    {
        var usage = new StringBuilder($"Usage: {program} [options]{(positionals is null ? "" : $" [{positionals}...]")}");
        usage.AppendLine();
        if (!string.IsNullOrEmpty(description))
            usage.AppendLine().AppendLine(description);

        var rows = options.Select(o => (
                Left: o.IsFlag ? $"--{o.Name}" : $"--{o.Name} <{o.Type}>",
                Right: string.Join(" ", new[] { o.Description, o.Required ? "(required)" : o.Default is null ? null : $"(default: {o.Default})" }.Where(s => !string.IsNullOrEmpty(s)))))
            .Append((Left: "-h, --help", Right: "Show this help"))
            .ToList();
        int width = rows.Max(r => r.Left.Length) + 2;
        usage.AppendLine().AppendLine("Options:");
        foreach (var (left, right) in rows)
            usage.AppendLine($"  {left.PadRight(width)}{right}".TrimEnd());
        return usage.ToString().TrimEnd();
    }

    private static string Article(string type) => type is "int" ? "an" : "a";

    /// <summary>Edit distance, to suggest the option a typo meant.</summary>
    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }
}

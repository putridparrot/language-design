using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Piper.Runtime;

/// <summary>
/// A persisted actor's state, kept now and then (<c>snapshot every 100;</c>) so that it starts from the latest one
/// and replays only the events since, rather than all of them. Its generated code writes each state variable with
/// <see cref="With{T}"/> and reads it back with <see cref="Get{T}"/>.
/// <para>
/// Each value is kept with its type's shape: its fields, and theirs, all the way down. A snapshot taken before the
/// state's types changed (a field added to a record) can't be read as the new ones, so <see cref="Get{T}"/> refuses
/// it rather than make up the new field's value, and the actor replays every event instead. The events are what's
/// kept; a snapshot is only a quicker way to the state they make.
/// </para>
/// </summary>
public sealed class PiperSnapshot
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly JsonObject _values;

    private PiperSnapshot(JsonObject values, bool found)
    {
        _values = values;
        Found = found;
    }

    /// <summary>A snapshot to write the state into.</summary>
    public PiperSnapshot() : this([], true)
    {
    }

    /// <summary>No snapshot: the stream has none, or its store doesn't keep them.</summary>
    public static PiperSnapshot None => new([], false);

    /// <summary>Whether there was a snapshot to restore.</summary>
    public bool Found { get; }

    /// <summary>Adds the state variable <paramref name="name"/>, written as its declared type, with that type's shape.</summary>
    public PiperSnapshot With<T>(string name, T value)
    {
        _values[name] = new JsonObject
        {
            ["shape"] = Shape(typeof(T)),
            ["value"] = JsonSerializer.SerializeToNode(value, Json),
        };
        return this;
    }

    /// <summary>
    /// The state variable <paramref name="name"/>, read as <typeparamref name="T"/> (the variable's type, from
    /// <paramref name="current"/>, its value before). A <see cref="PiperSnapshotException"/> if the snapshot hasn't
    /// it, or kept it as another shape.
    /// </summary>
    public T Get<T>(string name, T current)
    {
        _ = current;
        if (_values[name] is not JsonObject kept || kept["shape"]?.GetValue<string>() != Shape(typeof(T)))
            throw new PiperSnapshotException($"The snapshot's '{name}' isn't a {typeof(T).Name} as the actor's is now.");
        return kept["value"].Deserialize<T>(Json)!;
    }

    /// <summary>The snapshot as JSON, to keep.</summary>
    public string Write() => _values.ToJsonString();

    /// <summary>A kept snapshot.</summary>
    public static PiperSnapshot Read(string json) =>
        new(JsonNode.Parse(json) as JsonObject ?? throw new PiperSnapshotException("A snapshot isn't a JSON object."), true);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string> Shapes = new();

    /// <summary>A fingerprint of <paramref name="type"/>'s shape: what changes when JSON written as it couldn't be read as it.</summary>
    public static string Shape(Type type) => Shapes.GetOrAdd(type, t =>
    {
        var text = new StringBuilder();
        Describe(t, text, []);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
    });

    private static void Describe(Type type, StringBuilder text, HashSet<Type> seen)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            text.Append('?');
            Describe(underlying, text, seen);
            return;
        }
        text.Append(type.IsGenericType ? type.GetGenericTypeDefinition().FullName : type.FullName);
        if (type.IsArray)
        {
            Describe(type.GetElementType()!, text, seen);
            return;
        }
        if (type.IsGenericType)
        {
            text.Append('<');
            foreach (var argument in type.GetGenericArguments())
            {
                Describe(argument, text, seen);
                text.Append(',');
            }
            text.Append('>');
        }
        // .NET's own types (int, string, ImmutableList) are written as they always are; a program's, by their members.
        if (type.IsPrimitive || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true || !seen.Add(type))
            return;
        if (type.IsEnum)
        {
            text.Append('[').AppendJoin(',', Enum.GetNames(type)).Append(']');
            return;
        }
        text.Append('{');
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetMethod is not null && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            text.Append(property.Name).Append(':');
            Describe(property.PropertyType, text, seen);
            text.Append(';');
        }
        // A Piper enum's variants, each a type of its own deriving from it.
        foreach (var variant in type.GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => a.DerivedType)
                     .Concat(type.GetNestedTypes().Where(n => n.IsSubclassOf(type))).Distinct().OrderBy(v => v.Name, StringComparer.Ordinal))
        {
            text.Append('|');
            Describe(variant, text, seen);
        }
        text.Append('}');
    }
}

/// <summary>A snapshot that can't be restored as the actor's state is now: its events are replayed instead.</summary>
public sealed class PiperSnapshotException(string message) : Exception(message);

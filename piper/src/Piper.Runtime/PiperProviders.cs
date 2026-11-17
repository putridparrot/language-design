using System.Collections.Concurrent;

namespace Piper.Runtime;

/// <summary>
/// A provider's settings: the keys and values of its configuration section (<c>Events</c>: <c>Provider</c>,
/// <c>Connection</c>, …), matched without regard to case, as .NET's configuration is. Made from any configuration (an
/// <c>IConfigurationSection</c>'s <c>AsEnumerable(true)</c>, a dictionary), so the registry needs no configuration
/// library of its own.
/// </summary>
public sealed class ProviderSettings
{
    private readonly Dictionary<string, string?> _values;

    public ProviderSettings(string section, IEnumerable<KeyValuePair<string, string?>> values)
    {
        Section = section;
        _values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            if (key.Length > 0)
                _values[key] = value;
        }
    }

    /// <summary>The section the settings were read from, for messages: <c>Events</c>.</summary>
    public string Section { get; }

    /// <summary>The value of <paramref name="key"/>, or null (None) if it isn't set.</summary>
    public string? this[string key] => _values.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    /// <summary>The value of <paramref name="key"/>, which the provider needs: a <see cref="ProviderException"/> if it isn't set.</summary>
    public string Required(string key) =>
        this[key] ?? throw new ProviderException($"{Section}:{key} isn't set, and the {this["Provider"] ?? "chosen"} provider needs it.");

    /// <summary>The value of <paramref name="key"/> as a <see cref="Secret{T}"/>, for a connection string or a key.</summary>
    public Secret<string> RequiredSecret(string key) => new(Required(key));
}

/// <summary>A provider that can't be made: an unknown name, a missing setting, a package the app hasn't brought.</summary>
public sealed class ProviderException(string message) : Exception(message);

/// <summary>
/// Providers of a service, by name: what an app's configuration chooses among at startup, so a program moves between
/// clouds, or runs locally, by changing configuration, not code. A service is a trait or interface (<see cref="IEventStore"/>);
/// each provider is a function from its settings to an implementation. Every service has a local provider, used when
/// nothing is configured (<c>memory</c> for an <see cref="IEventStore"/>). Others come with the plugins a program
/// brings, which register theirs as the program starts.
/// <code>
/// // appsettings.json: { "Events": { "Provider": "postgres", "Connection": "Host=…" } }
/// let store = PiperProviders.Create&lt;IEventStore&gt;(ProviderSettings("Events", configuration.GetSection("Events").AsEnumerable(true)));
/// </code>
/// </summary>
public static class PiperProviders
{
    private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, Func<ProviderSettings, object>>> Registered = new();
    private static readonly ConcurrentDictionary<Type, string> Locals = new();

    static PiperProviders() => Register<IEventStore>("memory", _ => new MemoryEventStore(), local: true);

    /// <summary>
    /// Makes <paramref name="name"/> a provider of <typeparamref name="T"/>; a <paramref name="local"/> one is used when
    /// the settings name none. A name registered again is replaced.
    /// </summary>
    public static void Register<T>(string name, Func<ProviderSettings, T> create, bool local = false) where T : class
    {
        Registered.GetOrAdd(typeof(T), _ => new(StringComparer.OrdinalIgnoreCase))[name] = settings => create(settings);
        if (local)
            Locals[typeof(T)] = name;
    }

    /// <summary>
    /// The provider of <typeparamref name="T"/> the settings' <c>Provider</c> names, made from them; the local one if
    /// they name none. An unknown name is a <see cref="ProviderException"/> listing the names there are.
    /// </summary>
    public static T Create<T>(ProviderSettings settings) where T : class
    {
        var name = settings["Provider"] ?? (Locals.TryGetValue(typeof(T), out var local) ? local : null)
            ?? throw new ProviderException($"{settings.Section}:Provider isn't set, and {typeof(T).Name} has no local provider; it can be {Listed<T>()}.");
        if (!Registered.TryGetValue(typeof(T), out var providers) || !providers.TryGetValue(name, out var create))
            throw new ProviderException($"{settings.Section}:Provider is '{name}', which isn't a provider of {typeof(T).Name}: it can be {Listed<T>()}. " +
                                        "A provider comes with the plugin that brings it, in piper.json.");
        return (T)create(settings);
    }

    /// <summary>The names of <typeparamref name="T"/>'s providers, in order.</summary>
    public static IReadOnlyList<string> Names<T>() =>
        Registered.TryGetValue(typeof(T), out var providers) ? [.. providers.Keys.Order(StringComparer.OrdinalIgnoreCase)] : [];

    private static string Listed<T>() => Names<T>() is { Count: > 0 } names ? string.Join(", ", names) : "none yet";
}

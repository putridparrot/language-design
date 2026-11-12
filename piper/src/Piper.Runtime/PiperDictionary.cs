namespace Piper.Runtime;

/// <summary>Dictionary literals that spread another dictionary: compiled code calls this, as C# initializers can't spread.</summary>
public static class PiperDictionary
{
    /// <summary>
    /// <c>[...defaults, "port": 8080]</c>: the entries in order, a later one replacing an earlier one with the same key,
    /// as setting the indexer does.
    /// </summary>
    public static Dictionary<TKey, TValue> Of<TKey, TValue>(ReadOnlySpan<KeyValuePair<TKey, TValue>> entries) where TKey : notnull
    {
        var dictionary = new Dictionary<TKey, TValue>(entries.Length);
        foreach (var (key, value) in entries)
            dictionary[key] = value;
        return dictionary;
    }
}

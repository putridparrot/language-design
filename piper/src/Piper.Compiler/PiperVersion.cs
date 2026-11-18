using System.Reflection;

namespace Piper;

/// <summary>The version of this piper: the package's, so a local pack (1.0.0.463511) is told apart from the release.</summary>
public static class PiperVersion
{
    /// <summary>The version this compiler was packed as, without the commit the SDK appends.</summary>
    public static string Current { get; } =
        typeof(PiperVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ??
        typeof(PiperVersion).Assembly.GetName().Version?.ToString() ?? "?";
}

using Piper.Plugins;

namespace Piper.Plugins.OpenApi;

/// <summary>
/// OpenAPI documents for a web program, with schemas for Piper's types as they're written in JSON: it brings
/// Microsoft.AspNetCore.OpenApi and Piper.Web.OpenApi, and adds no syntax. A program calls
/// <c>builder.Services.AddPiperOpenApi()</c> and <c>app.MapOpenApi()</c>. It's a plugin of its own so that only programs
/// wanting OpenAPI carry the package.
/// </summary>
public sealed class OpenApiPlugin : IPiperPlugin
{
    public string Name => "openapi";

    public IReadOnlyList<PluginKeyword> Keywords => [];

    public IReadOnlyDictionary<string, string> Packages { get; } = new Dictionary<string, string> { ["Microsoft.AspNetCore.OpenApi"] = "10.0.13" };

    public IReadOnlyList<string> Frameworks => ["Microsoft.AspNetCore.App"];

    /// <summary>Piper.Web.OpenApi: the schema transformer, and <c>AddPiperOpenApi</c>.</summary>
    public IReadOnlyList<string> References => ["Piper.Web.OpenApi.dll"];
}

using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Piper.LanguageServer;
using Piper.Plugins;

namespace Piper.Tests;

/// <summary>
/// Plugins added with `piper plugin add`, which a piper.json names as it does those that come with piper, and which
/// the language server picks up while the editor is open. Each test uses a plugins folder of its own.
/// </summary>
public sealed class AddedPluginsTests : IDisposable
{
    private readonly string _previous = AddedPlugins.Directory;
    private readonly string _directory = Directory.CreateTempSubdirectory("piper-added-").FullName;

    public AddedPluginsTests() => AddedPlugins.Directory = Path.Combine(_directory, "plugins");

    public void Dispose()
    {
        AddedPlugins.Directory = _previous;
        Directory.Delete(_directory, recursive: true);
    }

    private static string Plugin(string folder, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins", folder));

    [Fact]
    public void An_added_plugin_is_named_in_piper_json_as_one_that_comes_with_piper_is()
    {
        var (name, error) = AddedPlugins.Add(Plugin("Piper.Plugins.Parallel"), "my-parallel");
        Assert.Null(error);
        Assert.Equal("my-parallel", name);
        Assert.Equal("my-parallel", Assert.Single(AddedPlugins.Names));

        var (plugins, errors) = PluginLoader.Load(_directory, ["my-parallel"]);
        Assert.Empty(errors);
        Assert.Equal("parallel", Assert.Single(plugins).Name);

        Assert.True(AddedPlugins.Remove("my-parallel"));
        Assert.False(AddedPlugins.Remove("my-parallel"));
        (_, errors) = PluginLoader.Load(_directory, ["my-parallel"]);
        Assert.Contains("Add it with 'piper plugin add <folder>'", Assert.Single(errors).Message);
    }

    [Fact]
    public void A_missing_plugins_error_lists_those_added()
    {
        AddedPlugins.Add(Plugin("Piper.Plugins.Parallel"), "my-parallel");
        var (_, errors) = PluginLoader.Load(_directory, ["nope"]);
        Assert.Contains("or one added with 'piper plugin add' (my-parallel)", Assert.Single(errors).Message);
    }

    [Theory]
    [InlineData(null, "comes with piper")]          // its own name, `parallel`, is taken by the bundled one
    [InlineData("a.b", "can't name a plugin")]
    public void Names_a_piper_json_couldnt_use_are_refused(string? name, string message)
    {
        var (added, error) = AddedPlugins.Add(Plugin("Piper.Plugins.Parallel"), name);
        Assert.Null(added);
        Assert.Contains(message, error);
        Assert.Empty(AddedPlugins.Names);
    }

    [Fact]
    public void A_folder_without_a_plugin_assembly_is_refused()
    {
        var (added, error) = AddedPlugins.Add(_directory);
        Assert.Null(added);
        Assert.Contains("has no plugin assembly", error);
    }

    [Fact]
    public async Task The_language_server_uses_a_plugin_added_while_the_editor_is_open()
    {
        var sent = new List<JsonObject>();
        var server = new PiperLanguageServer(message =>
        {
            sent.Add(message);
            return Task.CompletedTask;
        });
        await server.HandleAsync("initialize", new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["workspace"] = new JsonObject
                {
                    ["didChangeWatchedFiles"] = new JsonObject { ["dynamicRegistration"] = true, ["relativePatternSupport"] = true },
                    ["semanticTokens"] = new JsonObject { ["refreshSupport"] = true },
                },
            },
        });
        await server.HandleAsync("initialized", new JsonObject());

        // It asks the client to watch the plugins folder, which is outside the workspace.
        var registration = Assert.Single(sent, m => m["method"]?.GetValue<string>() == "client/registerCapability");
        var pattern = registration["params"]!["registrations"]![0]!["registerOptions"]!["watchers"]![0]!["globPattern"]!;
        Assert.Equal(new Uri(AddedPlugins.Directory).AbsoluteUri, pattern["baseUri"]!.GetValue<string>());

        var program = Path.Combine(_directory, "program");
        Directory.CreateDirectory(program);
        var project = Path.Combine(program, PiperProject.FileName);
        File.WriteAllText(project, """{ "plugins": ["my-parallel"] }""");
        var main = Path.Combine(program, "main.piper");
        var text = "fn Main() {\n    let items = [1, 2];\n    parallel for i in items { }\n}\n";
        File.WriteAllText(main, text);
        await server.HandleAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = PiperLanguageServer.UriOf(main), ["languageId"] = "piper", ["version"] = 1, ["text"] = text },
        });
        int ProjectErrors() => sent.Last(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics" &&
                                             PiperLanguageServer.UriOf(project) == m["params"]!["uri"]!.GetValue<string>())["params"]!["diagnostics"]!.AsArray().Count;
        Assert.Equal(1, ProjectErrors());

        // The plugin is added, and the client tells the server its assembly changed.
        sent.Clear();
        AddedPlugins.Add(Plugin("Piper.Plugins.Parallel"), "my-parallel");
        var assembly = Directory.GetFiles(Path.Combine(AddedPlugins.Directory, "my-parallel"), "*.dll")[0];
        await server.HandleAsync("workspace/didChangeWatchedFiles", new JsonObject
        {
            ["changes"] = new JsonArray(new JsonObject { ["uri"] = PiperLanguageServer.UriOf(assembly), ["type"] = 1 }),
        });
        Assert.Equal(0, ProjectErrors());
        Assert.Contains(sent, m => m["method"]?.GetValue<string>() == "workspace/semanticTokens/refresh");
    }
}

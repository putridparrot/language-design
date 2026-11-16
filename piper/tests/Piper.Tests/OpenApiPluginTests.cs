using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The openapi plugin (plugins/Piper.Plugins.OpenApi): an OpenAPI document whose schemas describe Piper's types as Piper
/// writes them in JSON. Microsoft.AspNetCore.OpenApi is restored as a program's package would be (from the NuGet cache
/// when it's there).
/// </summary>
public class OpenApiPluginTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(),
            ["Piper.Plugins.Web/bin/plugin/Piper.Plugins.Web.dll", "Piper.Plugins.OpenApi/bin/plugin/Piper.Plugins.OpenApi.dll"]);
        Assert.Empty(errors);
        var packages = plugins.SelectMany(p => p.Packages).ToDictionary(p => p.Key, p => p.Value);
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.Combine(Path.GetTempPath(), "piper-openapi-tests"), packages,
            [.. plugins.SelectMany(p => p.Frameworks).Distinct()], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private const string Program = """"
        using System;
        using System.Net.Http;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Hosting;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Logging;
        using Piper.Web;
        using Piper.Web.OpenApi;

        newtype BookId = int;

        record Book(id: BookId, title: string, subtitle: string?, pages: int?);

        record Loan(book: BookId, days: int) requires days > 0;

        [Status(409)]
        enum LoanError {
            [Status(404)] UnknownBook(id: BookId),
            OutOnLoan(until: string),
            Closed,
        }

        enum Shelf { Fiction, Science }

        record Placement(book: BookId, shelf: Shelf);

        fn Find(id: BookId) => Book? = None;
        fn Lend(loan: Loan) => Result<Loan, LoanError> = Err(LoanError.Closed);
        fn Place(id: BookId) => Placement = Placement(id, Shelf.Fiction);
        fn LastError() => LoanError = LoanError.Closed;

        endpoints Library "/api" {
            get "/books/{id:int}" (id: BookId) => Find(id);
            post "/loans" (loan: Loan) => Lend(loan);
            get "/placements/{id:int}" (id: BookId) => Place(id);
            get "/errors/last" () => LastError();
            get "/books" (page: int?, shelf: Shelf?, after: BookId, by: string?) => Place(after);
        }

        async fn Main() {
            let builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddPiperOpenApi();
            let urls: string[] = ["http://127.0.0.1:0"];
            builder.WebHost.UseUrls(urls);
            let app = builder.Build();
            app.MapOpenApi();
            Library(app);
            await app.StartAsync();
            using let client = HttpClient();
            client.BaseAddress = Uri(app.Urls.First());
            Print(await client.GetStringAsync("/openapi/v1.json"));
            await app.StopAsync();
        }
        """";

    private static JsonElement Document()
    {
        var result = Compilation.Compile([new SourceText(Program.Replace("app.Urls.First()", "System.Linq.Enumerable.First(app.Urls)"), "main.piper")],
            new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.Run(result.Files, references: result.References);
        Assert.True(run.Success, string.Join("\n", run.Errors));
        return JsonDocument.Parse(run.Output).RootElement;
    }

    private static readonly Lazy<JsonElement> Generated = new(Document);

    private static JsonElement Schema(string name) => Generated.Value.GetProperty("components").GetProperty("schemas").GetProperty(name);

    /// <summary>
    /// A schema's types, sorted, following a $ref. ASP.NET Core's "string" beside "integer" is left out: web JSON reads a
    /// number from a string too, which isn't Piper's doing.
    /// </summary>
    private static string Types(JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out var reference))
            return Types(Schema(reference.GetString()!.Split('/')[^1]));
        var types = schema.GetProperty("type") is { ValueKind: JsonValueKind.Array } many
            ? many.EnumerateArray().Select(t => t.GetString()!).ToList()
            : [schema.GetProperty("type").GetString()!];
        if (types.Contains("integer") || types.Contains("number"))
            types.Remove("string");
        return string.Join(",", types.Order());
    }

    [Fact]
    public void An_optional_field_is_its_value_or_null_and_isnt_required()
    {
        var book = Schema("Book");
        var properties = book.GetProperty("properties");
        Assert.Equal("null,string", Types(properties.GetProperty("subtitle")));
        Assert.Equal("integer,null", Types(properties.GetProperty("pages")));
        var required = book.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToHashSet();
        Assert.Contains("title", required);
        Assert.DoesNotContain("subtitle", required);
    }

    [Fact]
    public void A_newtype_is_its_value()
    {
        Assert.Equal("integer", Types(Schema("Book").GetProperty("properties").GetProperty("id")));
    }

    [Fact]
    public void A_record_with_invariants_is_an_object_of_its_fields()
    {
        var loan = Schema("Loan");
        Assert.Equal("object", Types(loan));
        Assert.Equal("integer", Types(loan.GetProperty("properties").GetProperty("days")));
        Assert.Equal(["book", "days"], loan.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).Order());
    }

    [Fact]
    public void An_enum_is_one_of_its_variants()
    {
        var variants = Schema("LoanError").GetProperty("oneOf").EnumerateArray().ToList();
        Assert.Equal(3, variants.Count);
        var unknown = variants[0];
        Assert.Equal("UnknownBook", unknown.GetProperty("properties").GetProperty("kind").GetProperty("enum")[0].GetString());
        Assert.Equal("integer", Types(unknown.GetProperty("properties").GetProperty("id")));
        Assert.Equal("Closed", variants[2].GetProperty("enum")[0].GetString());

        // An enum of fieldless variants is one of their names.
        var shelf = Schema("Shelf");
        Assert.Equal("string", Types(shelf));
        Assert.Equal(["Fiction", "Science"], shelf.GetProperty("enum").EnumerateArray().Select(v => v.GetString()!));
    }

    [Fact]
    public void A_query_parameter_is_described_as_the_value_it_takes()
    {
        // Options and newtypes are bound by their TryParse, which ASP.NET Core describes as a string.
        var parameters = Generated.Value.GetProperty("paths").GetProperty("/api/books").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().ToDictionary(p => p.GetProperty("name").GetString()!);
        bool Required(string name) => parameters[name].TryGetProperty("required", out var r) && r.GetBoolean();

        Assert.Equal("integer", Types(parameters["page"].GetProperty("schema")));
        Assert.False(Required("page"));
        Assert.Equal("string", Types(parameters["shelf"].GetProperty("schema")));
        Assert.Equal(["Fiction", "Science"], ResolvedEnum(parameters["shelf"].GetProperty("schema")));
        Assert.False(Required("shelf"));
        Assert.Equal("integer", Types(parameters["after"].GetProperty("schema")));
        Assert.True(Required("after"));
        Assert.Equal("string", Types(parameters["by"].GetProperty("schema")));
        Assert.False(Required("by"));
    }

    private static List<string> ResolvedEnum(JsonElement schema) =>
        (schema.TryGetProperty("$ref", out var reference) ? Schema(reference.GetString()!.Split('/')[^1]) : schema)
            .GetProperty("enum").EnumerateArray().Select(v => v.GetString()!).ToList();

    [Fact]
    public void An_optional_result_is_its_value_and_a_404()
    {
        var get = Generated.Value.GetProperty("paths").GetProperty("/api/books/{id}").GetProperty("get");
        var responses = get.GetProperty("responses");
        Assert.True(responses.TryGetProperty("404", out _));
        var schema = responses.GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("#/components/schemas/Book", schema.GetProperty("$ref").GetString());
        Assert.Equal(["Library"], get.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!));
    }
}

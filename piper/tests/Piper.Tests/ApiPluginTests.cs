using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Piper.Plugins;
using Piper.Text;

namespace Piper.Tests;

/// <summary>
/// The api plugin (plugins/Piper.Plugins.Api): an <c>api</c> declaration's typed client, and the web plugin's check that
/// a server's <c>endpoints Name: Api</c> answers it. The server is served from memory (Piper.Web's TestClient, an
/// HttpClient), so the client's requests go through both ends.
/// </summary>
public class ApiPluginTests
{
    private static readonly Lazy<(ImmutableArray<IPiperPlugin> Plugins, ProgramReferences References)> Plugins = new(() =>
    {
        var (plugins, errors) = PluginLoader.Load(PluginsDirectory(),
            ["Piper.Plugins.Api/bin/plugin/Piper.Plugins.Api.dll", "Piper.Plugins.Web/bin/plugin/Piper.Plugins.Web.dll"]);
        Assert.Empty(errors);
        var (references, referenceErrors) = ProgramReferences.Resolve(
            Path.GetTempPath(), ImmutableDictionary<string, string>.Empty, [.. plugins.SelectMany(p => p.Frameworks)], [.. plugins.SelectMany(PluginLoader.References)]);
        Assert.Empty(referenceErrors);
        return (plugins, references);
    });

    private static string PluginsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "plugins"));

    private static CompilationResult Compile(string text, bool tests = false) =>
        Compilation.Compile([new SourceText(text, "main.piper")],
            new CompilationOptions(LineDirectives: tests, IncludeTests: tests) { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References });

    private const string Shop = """
        using System;
        using System.Collections.Generic;
        using System.Net;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Builder;
        using Piper.Web;

        newtype ItemId = int;
        enum Size { Small, Large }
        record Item(id: ItemId, name: string, size: Size, price: decimal);
        record NewItem(name: string, size: Size, price: decimal);

        enum ShopError {
            [Status(404)] NoItem(id: ItemId),
            Invalid(reason: string),
        }

        api ShopApi "/api" {
            fn Items(size: Size?, cheaperThan: decimal?) => List<Item> = get "/items";
            fn Item(id: ItemId) => Item? = get "/items/{id:int}";
            fn Add(item: NewItem) => Result<Item, ShopError> = post "/items";
            fn Rename(id: ItemId, name: string) => Result<Item, ShopError> = put "/items/{id:int}/name";
            fn Greeting() => string = get "/hello";
        }

        class Store {
            public mut items: List<Item> = [Item(ItemId(1), "kettle", Size.Small, 20m), Item(ItemId(2), "sofa", Size.Large, 400m)];
        }

        fn Find(store: Store, id: ItemId) => Item? {
            for item in store.items { if item.id == id { return Some(item); } }
            None
        }

        fn Matches(item: Item, size: Size?, cheaperThan: decimal?) => bool {
            if size is Some(wanted) && item.size != wanted { return false; }
            if cheaperThan is Some(limit) && item.price >= limit { return false; }
            true
        }

        fn Add(store: Store, item: NewItem) => Result<Item, ShopError> {
            if item.price <= 0m { return Err(ShopError.Invalid("A price must be more than 0.")); }
            let added = Item(ItemId(store.items.Count + 1), item.name, item.size, item.price);
            store.items.Add(added);
            Ok(added)
        }

        fn Rename(store: Store, id: ItemId, name: string) => Result<Item, ShopError> = match Find(store, id) {
            Some(item) => Ok({ ...item, name: name }),
            None => Err(ShopError.NoItem(id)),
        };

        endpoints Endpoints: ShopApi "/api" {
            get "/items" (size: Size?, cheaperThan: decimal?, store: Store) => store.items.FindAll(i => Matches(i, size, cheaperThan));
            get "/items/{id:int}" (id: ItemId, store: Store) => Find(store, id);
            post "/items" (item: NewItem, store: Store) => Add(store, item);
            put "/items/{id:int}/name" (id: ItemId, name: string, store: Store) => Rename(store, id, name);
            get "/hello" () => "hello";
        }

        server ShopServer(store: Store) {
            logging off;
            singleton Store = store;
            problems;
            endpoints Endpoints;
        }

        fn Main() {}
        """;

    /// <summary>
    /// The client sends each call as its server reads it (route values, the query, a JSON body) and reads the answer
    /// back as the handler gave it: a list, an Option (None for the 404), a Result's value or its own error (rebuilt from
    /// the ProblemDetails), and a string.
    /// </summary>
    [Fact]
    public void A_client_calls_its_server()
    {
        var result = Compile(Shop + """

            async test "the client calls the server" {
                using let site = await TestClient.Start(() => ShopServer(Store()));
                let shop = ShopApi(site);

                let Ok(all) = await shop.Items(None, None) else { Assert(false, "listing failed"); return; };
                AssertEqual(2, all.Count);
                let Ok(small) = await shop.Items(Some(Size.Small), None) else { Assert(false, "listing small failed"); return; };
                AssertEqual(["kettle"], small.ConvertAll(i => i.name));
                let Ok(cheap) = await shop.Items(None, Some(100m)) else { Assert(false, "listing cheap failed"); return; };
                AssertEqual(["kettle"], cheap.ConvertAll(i => i.name));

                let Ok(sofa) = await shop.Item(ItemId(2)) else { Assert(false, "finding failed"); return; };
                AssertEqual("sofa", sofa?.name ?? "");
                let Ok(missing) = await shop.Item(ItemId(9)) else { Assert(false, "a 404 wasn't None"); return; };
                Assert(missing is None);

                let Ok(lamp) = await shop.Add(NewItem("lamp", Size.Small, 35m)) else { Assert(false, "adding failed"); return; };
                AssertEqual(ItemId(3), lamp.id);
                let Err(free) = await shop.Add(NewItem("free", Size.Small, 0m)) else { Assert(false, "a free item was added"); return; };
                AssertEqual(400, free.Status);
                AssertEqual(Some(ShopError.Invalid("A price must be more than 0.")), free.Error);
                let Err(chair) = await shop.Rename(ItemId(7), "chair") else { Assert(false, "a missing item was renamed"); return; };
                AssertEqual(404, chair.Status);
                AssertEqual(Some(ShopError.NoItem(ItemId(7))), chair.Error);

                let Ok(renamed) = await shop.Rename(ItemId(2), "big sofa") else { Assert(false, "renaming failed"); return; };
                AssertEqual("big sofa", renamed.name);
                let Ok(greeting) = await shop.Greeting() else { Assert(false, "greeting failed"); return; };
                AssertEqual("hello", greeting);
            }

            async test "a server that can't be reached is an error, not an exception" {
                let http = System.Net.Http.HttpClient();
                http.BaseAddress = Uri("http://127.0.0.1:1/");
                let shop = ShopApi(http);
                let Err(e) = await shop.Greeting() else { Assert(false, "nothing should answer"); return; };
                AssertEqual(0, e.Status);
            }
            """, tests: true);
        Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        var run = Runner.RunTests(result.Files, references: result.References);
        Assert.Empty(run.Errors);
        Assert.True(run.Results.All(r => r.Passed), string.Join("\n", run.Results.Select(r => $"{r.Name}: {r.Failure}")));
        Assert.Equal(2, run.Results.Length);
    }

    /// <summary>
    /// A server's endpoints must answer their api exactly: every call, and nothing else; each value the call sends, by
    /// name and type; nothing more the client would have to send; and the call's type.
    /// </summary>
    [Theory]
    [InlineData("""get "/hello" () => "hello"; get "/extra" () => "x";""", "'GET /api/extra' isn't one of ShopApi's calls")]
    [InlineData("", "ShopApi's call Greeting (GET /api/hello) has no endpoint in Endpoints, so it would answer 404: add get \"/hello\" to Endpoints.")]
    [InlineData("""get "/hello" () => 42;""", "ShopApi.Greeting answers string, but this handler answers int.")]
    [InlineData("""get "/hello" (name: string) => name;""", "This handler takes 'name' (string), which ShopApi.Greeting doesn't send")]
    [InlineData("""get "/hello" (name: string?) => name ?? "hello";""", null)]
    [InlineData("""get "/hello/{who}" (who: string) => who;""", "'GET /api/hello/{who}' isn't one of ShopApi's calls")]
    public void Endpoints_must_answer_their_api(string greeting, string? expected)
    {
        var program = Shop.Replace("""get "/hello" () => "hello";""", greeting);
        var result = Compile(program);
        if (expected is null)
            Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
        else
            Assert.Contains(result.FormattedDiagnostics, d => d.Contains(expected));
    }

    /// <summary>A call's values must be what the handler takes, by name and type; and the api must exist.</summary>
    [Theory]
    [InlineData("""put "/items/{id:int}/name" (id: int, name: string, store: Store) => Rename(store, ItemId(id), name);""",
        "ShopApi.Rename sends 'id' as ItemId, but this handler takes it as int.")]
    [InlineData("""put "/items/{key:int}/name" (key: ItemId, name: string, store: Store) => Rename(store, key, name);""",
        "ShopApi.Rename sends 'id' (ItemId), which this handler doesn't take: add (id: ItemId).")]
    [InlineData("""put "/items/{id:int}/name" (id: ItemId, name: string, store: Store) => Find(store, id);""",
        "ShopApi.Rename answers Result<Item, ShopError>, but this handler answers Item?.")]
    public void A_handler_must_take_and_answer_what_its_call_does(string rename, string expected)
    {
        var program = Shop.Replace("""put "/items/{id:int}/name" (id: ItemId, name: string, store: Store) => Rename(store, id, name);""", rename);
        Assert.Contains(Compile(program).FormattedDiagnostics, d => d.Contains(expected));
    }

    [Fact]
    public void Endpoints_name_an_api_that_exists()
    {
        var result = Compile(Shop.Replace("endpoints Endpoints: ShopApi", "endpoints Endpoints: ShopAPI"));
        Assert.Contains(result.FormattedDiagnostics, d => d.Contains("There's no api named ShopAPI"));
        result = Compile(Shop.Replace("endpoints Endpoints: ShopApi", "endpoints Endpoints: Store"));
        Assert.Contains(result.FormattedDiagnostics, d => d.Contains("Store isn't an api"));
    }

    /// <summary>Mistakes in an api are reported where they're written.</summary>
    [Theory]
    [InlineData("""fn Items() => List<Item> = get "/items"; fn Items() => List<Item> = get "/all";""", "There's already a call named 'Items' in Bad.")]
    [InlineData("""fn A() => int = get "/a"; fn B() => int = get "/a";""", "'get /a' is already a call of Bad.")]
    [InlineData("""fn A() => int = get "/a/{id}";""", "The route value '{id}' isn't one of A's parameters")]
    [InlineData("""fn A(item: NewItem) => int = get "/a";""", "A GET request has no body, so 'item' must be a route or query value")]
    [InlineData("""fn A(a: NewItem, b: NewItem) => int = post "/a";""", "A already sends a body")]
    [InlineData("""fn A(x) => int = get "/a";""", "Give 'x' a type")]
    [InlineData("""fn A() => int = fetch "/a";""", "Expected 'get', 'post', 'put', 'delete' or 'patch'")]
    [InlineData("""get "/a" () => 1;""", "Expected a call, such as fn Item(id: int)")]
    public void Mistakes_in_an_api_are_reported(string calls, string expected)
    {
        var result = Compile($$"""
            record NewItem(name: string);
            api Bad {
                {{calls}}
            }
            fn Main() {}
            """);
        Assert.Contains(result.FormattedDiagnostics, d => d.Contains(expected));
    }

    private const string Catalogue = """
        using System.Collections.Generic;

        newtype BookId = int;
        enum Format { Paper, Ebook }
        enum Lookup { Title(text: string), Author(name: string) }
        record Book(id: BookId, title: string, format: Format, subtitle: string?);
        record NewBook(title: string, format: Format);
        enum ShelfError { Missing(id: BookId) }

        api Shelf "/api" {
            fn Books(q: string?) => List<Book> = get "/books";
            fn Book(id: BookId) => Book? = get "/books/{id:int}";
            fn Add(book: NewBook) => Result<Book, ShelfError> = post "/books";
            fn Find(by: Lookup) => List<Book> = post "/find";
        }

        fn Main() {}
        """;

    /// <summary>Compiles <paramref name="program"/> from a file in <paramref name="folder"/>, as the CLI would.</summary>
    private static CompilationResult CompileIn(string folder, string program, WireShapeMode mode)
    {
        var path = Path.Combine(folder, "shelf.piper");
        File.WriteAllText(path, program);
        return Compilation.Compile([new SourceText(program, path)],
            new CompilationOptions { Plugins = Plugins.Value.Plugins, References = Plugins.Value.References, WireShapes = mode });
    }

    /// <summary>
    /// The first build records an api's shape beside it, in Name.shape.json; a compatible change (a new call, a field
    /// callers may leave out, a field they'll ignore) records the new one; and checking alone never writes.
    /// </summary>
    [Fact]
    public void An_apis_shape_is_recorded_and_kept_up_to_date()
    {
        var folder = Directory.CreateTempSubdirectory("piper-shape").FullName;
        try
        {
            var shape = Path.Combine(folder, "Shelf.shape.json");
            Assert.True(CompileIn(folder, Catalogue, WireShapeMode.Check).Success);
            Assert.False(File.Exists(shape));
            // Its types aren't known while it has errors, so nothing is recorded.
            Assert.False(CompileIn(folder, Catalogue.Replace("title: string, format", "title: Strnig, format"), WireShapeMode.Record).Success);
            Assert.False(File.Exists(shape));

            var baseline = CompileIn(folder, Catalogue, WireShapeMode.Record);
            Assert.True(baseline.Success, string.Join("\n", baseline.FormattedDiagnostics));
            var first = File.ReadAllText(shape);
            Assert.Contains("\"GET /api/books/{int}\"", first);
            Assert.Contains("\"output\": \"Result<Book, ShelfError>\"", first);
            Assert.Contains("\"sent\": \"both\"", first);        // BookId: sent, and answered

            var grown = Catalogue
                .Replace("""fn Books(q: string?) => List<Book> = get "/books";""", """fn Books(q: string?, page: int?) => List<Book> = get "/books"; fn Count() => int = get "/count";""")
                .Replace("record NewBook(title: string, format: Format);", "record NewBook(title: string, format: Format, notes: string?, copies: int = 1);")
                .Replace("record Book(id: BookId, title: string, format: Format, subtitle: string?);", "record Book(id: BookId, title: string, format: Format, pages: int);");
            var result = CompileIn(folder, grown, WireShapeMode.Check);
            Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
            Assert.Equal(first, File.ReadAllText(shape));

            Assert.True(CompileIn(folder, grown, WireShapeMode.Record).Success);
            var second = File.ReadAllText(shape);
            Assert.Contains("\"GET /api/count\"", second);
            Assert.Contains("\"copies\": \"int = default\"", second);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A change that would break clients built against the recorded shape is an error, and isn't recorded.</summary>
    [Theory]
    [InlineData("""fn Book(id: BookId) => Book? = get "/books/{id:int}";""", "",
        "nothing answers GET /api/books/{int} (Book) any more, but callers built before still send it")]
    [InlineData("""get "/books/{id:int}";""", """get "/book/{id:int}";""", "nothing answers GET /api/books/{int} (Book)")]
    [InlineData("""fn Books(q: string?)""", """fn Books(q: string?, page: int)""", "Books now needs 'page' (int), which callers built before don't send")]
    [InlineData("""fn Books(q: string?)""", """fn Books(q: int?)""", "Books takes 'q' as int?, but callers built before send string?")]
    [InlineData("""fn Book(id: BookId) => Book?""", """fn Book(id: BookId) => Book""", "Book answers Book, but callers built before read Book?")]
    [InlineData("record NewBook(title: string, format: Format);", "record NewBook(title: string, format: Format, isbn: string);",
        "NewBook has a new field, 'isbn', which callers built before don't send")]
    [InlineData("record Book(id: BookId, title: string, format: Format, subtitle: string?);", "record Book(id: BookId, format: Format, subtitle: string?);",
        "Book's field 'title' is gone, but callers built before need it")]
    [InlineData("record Book(id: BookId, title: string, format: Format, subtitle: string?);", "record Book(id: BookId, title: int, format: Format, subtitle: string?);",
        "Book's field 'title' is now int, not string")]
    [InlineData("enum Format { Paper, Ebook }", "enum Format { Paper, Ebook, Audio }", "Format has a new variant, Audio, which callers built before can't read")]
    [InlineData("enum Lookup { Title(text: string), Author(name: string) }", "enum Lookup { Title(text: string) }", "Lookup.Author is gone, but callers built before may still send it")]
    [InlineData("newtype BookId = int;", "newtype BookId = string;", "BookId is now a string, not a int")]
    [InlineData("record NewBook(title: string, format: Format);", "enum NewBook { Draft(title: string, format: Format) }", "NewBook is now an enum, not a record")]
    public void A_breaking_change_to_an_api_is_an_error(string before, string after, string expected)
    {
        var folder = Directory.CreateTempSubdirectory("piper-shape").FullName;
        try
        {
            var path = Path.Combine(folder, "Shelf.shape.json");
            var baseline = CompileIn(folder, Catalogue, WireShapeMode.Record);
            Assert.True(baseline.Success, string.Join("\n", baseline.FormattedDiagnostics));
            var recorded = File.ReadAllText(path);
            var changed = Catalogue.Replace(before, after);
            Assert.NotEqual(Catalogue, changed);

            var result = CompileIn(folder, changed, WireShapeMode.Record);
            Assert.Contains(result.FormattedDiagnostics, d => d.Contains($"A breaking change to Shelf: {expected}") && d.Contains("--accept-breaking"));
            Assert.Equal(recorded, File.ReadAllText(path));

            result = CompileIn(folder, changed, WireShapeMode.AcceptBreaking);
            Assert.True(result.Success, string.Join("\n", result.FormattedDiagnostics));
            Assert.NotEqual(recorded, File.ReadAllText(path));
            Assert.True(CompileIn(folder, changed, WireShapeMode.Check).Success);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_shape_file_that_cant_be_read_is_an_error()
    {
        var folder = Directory.CreateTempSubdirectory("piper-shape").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "Shelf.shape.json"), "{ not json");
            Assert.Contains(CompileIn(folder, Catalogue, WireShapeMode.Record).FormattedDiagnostics,
                d => d.Contains("Shelf.shape.json isn't a shape Piper can read; delete it to record Shelf's shape afresh."));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

using Piper;
using Piper.Plugins;
using Piper.Text;

/// <summary>
/// <c>piper db migrate</c> and <c>piper db status</c>: a program's databases' migrations (see
/// <see cref="DatabaseMigrations"/>). The program is compiled first, as <c>piper build</c> compiles it, which records
/// each database's shape (and stops at a breaking change to it, unless accepted).
/// </summary>
internal static class Database
{
    public const string Usage = """
        Usage:
          piper db migrate <folder> [--name <name>] [--accept-breaking]
                       Write the next migration of each database the program declares, if its shape changed since the
                       last: a SQL script per provider whose EF Core package the program has, beside the declaration
                       (<Name>.migrations/sqlite/0002-<name>.sql), to review and check in. The first is the baseline.
          piper db migrate <folder> --apply --connection <connection> [--provider <provider>] [--database <Name>]
                       Then apply the migrations a database doesn't have yet (provider: sqlite, the default; sqlserver;
                       postgres). Each runs in a transaction, recorded in its PiperMigrations table.
          piper db status <folder> [--connection <connection>] [--provider <provider>] [--database <Name>]
                       List the migrations, and with a connection, which the database has.
        """;

    public static int Run(string[] arguments)
    {
        if (arguments is not [("migrate" or "status") and var command, var folder, .. var options] || folder.StartsWith('-'))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        string? name = null, connection = null, provider = null, only = null;
        bool apply = false, acceptBreaking = false;
        for (int i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--name" when i + 1 < options.Length && command == "migrate":
                    name = options[++i];
                    break;
                case "--connection" when i + 1 < options.Length:
                    connection = options[++i];
                    break;
                case "--provider" when i + 1 < options.Length:
                    provider = options[++i];
                    break;
                case "--database" when i + 1 < options.Length:
                    only = options[++i];
                    break;
                case "--apply" when command == "migrate":
                    apply = true;
                    break;
                case "--accept-breaking" when command == "migrate":
                    acceptBreaking = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument '{options[i]}'.");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        if (apply && connection is null)
        {
            Console.Error.WriteLine("--apply needs the database's --connection.");
            return 2;
        }
        if (!Directory.Exists(folder) || !File.Exists(Path.Combine(folder, PiperProject.FileName)))
        {
            Console.Error.WriteLine($"{folder} isn't a program folder: it needs a {PiperProject.FileName} naming the data plugin.");
            return 2;
        }

        // Building records each database's shape, as `piper build` does: a breaking change stops here, unless accepted.
        var (loaded, errors) = LoadedProject.Load(folder);
        foreach (var error in errors)
            Console.Error.WriteLine(error.Format());
        if (loaded is null)
            return 2;
        var paths = loaded.SharedFiles.Concat(LoadedProject.SourceFiles(folder)).ToList();
        var sources = paths.Select(path => new SourceText(File.ReadAllText(path), Path.GetFullPath(path))).ToList();
        var result = Compilation.Compile(sources, new CompilationOptions
        {
            Plugins = loaded.Plugins,
            References = loaded.References,
            SharedFiles = [.. sources.Take(loaded.SharedFiles.Length).Select(s => s.FileName)],
            WireShapes = command == "status" ? WireShapeMode.Check : acceptBreaking ? WireShapeMode.AcceptBreaking : WireShapeMode.Record,
        });
        foreach (var diagnostic in result.FormattedDiagnostics)
            Console.Error.WriteLine(diagnostic);
        if (!result.Success)
            return 1;

        // The databases: the shapes the data plugin recorded beside the program's files.
        var databases = DatabaseMigrations.Databases(sources.Select(s => s.FileName)).Where(d => only is null || d.Name == only).ToList();
        if (databases.Count == 0)
        {
            Console.Error.WriteLine(only is null
                ? "The program declares no database (with the data plugin's `database Name { … }`)."
                : $"The program declares no database '{only}'.");
            return 1;
        }
        if (connection is not null && databases.Count > 1)
        {
            Console.Error.WriteLine($"The program has several databases ({string.Join(", ", databases.Select(d => d.Name))}): say which with --database.");
            return 2;
        }

        try
        {
            var chosen = provider ?? "sqlite";
            foreach (var database in databases)
            {
                if (command == "migrate")
                {
                    var written = DatabaseMigrations.Write(loaded.References, database, name);
                    foreach (var warning in written.Warnings)
                        Console.Error.WriteLine($"warning: {warning}");
                    foreach (var file in written.Files)
                        Console.WriteLine($"Wrote {file}");
                    if (written.Files.IsEmpty)
                        Console.WriteLine($"{database.Name}: no change since migration {database.Recorded()[^1].Id}.");
                    if (apply)
                    {
                        foreach (var line in DatabaseMigrations.Apply(loaded.References, database, chosen, connection!))
                            Console.WriteLine(line);
                    }
                    continue;
                }

                // Without a connection, the migrations and their providers' scripts; with one, which the database has.
                Console.WriteLine(connection is null ? $"{database.Name}:" : $"{database.Name} ({chosen}):");
                if (connection is null)
                {
                    foreach (var migration in database.Recorded())
                    {
                        var providers = Directory.GetDirectories(database.Migrations)
                            .Where(p => File.Exists(Path.Combine(p, $"{migration.Id}.sql"))).Select(Path.GetFileName);
                        Console.WriteLine($"  {migration.Id,-40} {string.Join(", ", providers)}");
                    }
                }
                else
                {
                    foreach (var line in DatabaseMigrations.Status(loaded.References, database, chosen, connection))
                        Console.WriteLine(line);
                }
                if (database.Recorded().Count == 0)
                    Console.WriteLine("  no migrations yet: `piper db migrate` writes the first, the baseline");
                else if (database.Changed(loaded.References))
                    Console.WriteLine("  its shape has changed since the last migration: `piper db migrate` writes the next");
            }
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

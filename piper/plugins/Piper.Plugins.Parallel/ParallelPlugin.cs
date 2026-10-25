using Piper.Plugins;
using Piper.Syntax;

namespace Piper.Plugins.Parallel;

/// <summary>
/// Adds two statements, each rewritten into ordinary Piper that calls .NET's task library:
/// <list type="bullet">
/// <item><c>parallel for x in items { … }</c> runs the body for each item at once: <c>Parallel.ForEach(items, x => { … })</c>.</item>
/// <item><c>concurrency { a(); b(); }</c> runs each statement at once and waits for them all:
/// <c>Task.WaitAll(Task.Run(() => { a(); }), Task.Run(() => { b(); }))</c>. In async code the statements can await,
/// and it's <c>await Task.WhenAll(Task.Run(async () => { … }), …)</c>.</item>
/// </list>
/// </summary>
public sealed class ParallelPlugin : IPiperPlugin
{
    public string Name => "parallel";

    public IReadOnlyList<PluginKeyword> Keywords { get; } =
    [
        new StatementKeyword("parallel", ParallelFor),
        new StatementKeyword("concurrency", Concurrency),
    ];

    /// <summary>parallelFor = "parallel" "for" IDENT "in" expression block ;</summary>
    private static IEnumerable<StatementSyntax> ParallelFor(PluginParser p)
    {
        p.Expect(TokenKind.ForKeyword);
        var variable = p.Expect(TokenKind.Identifier);
        p.Expect(TokenKind.InKeyword);
        var items = p.ParseExpressionBeforeBlock();
        var body = p.ParseBlock();
        return [p.Template.Statement("System.Threading.Tasks.Parallel.ForEach($items, $x => $body);",
            ("items", items), ("x", variable), ("body", body))];
    }

    /// <summary>concurrency = "concurrency" block ;   each of the block's statements runs as a task.</summary>
    private static IEnumerable<StatementSyntax> Concurrency(PluginParser p)
    {
        var block = p.ParseBlock();
        var statements = block.Trailing is null
            ? block.Statements
            : block.Statements.Add(new ExpressionStatementSyntax(block.Trailing.Span, block.Trailing));
        if (statements.IsEmpty)
            p.Report(block.Span, "A concurrency block needs statements to run.");

        foreach (var statement in statements.Where(s => s is VariableDeclarationSyntax or DeconstructionDeclarationSyntax or LetElseStatementSyntax))
            p.Report(statement.Span, "Each statement in a concurrency block runs on its own, so a variable declared in one can't be used; declare it before the block.");

        // In async code the tasks are awaited, and in a trailing closure too if a statement awaits (which makes the
        // closure async anyway); elsewhere they're waited for. A statement that awaits runs as an async task.
        bool awaits = p.AsyncContext == AsyncContext.Async || (p.AsyncContext == AsyncContext.IfAwaited && statements.Any(SyntaxWalker.Awaits));
        var tasks = statements.Select(statement => p.Template.Expression(
            awaits && SyntaxWalker.Awaits(statement)
                ? "System.Threading.Tasks.Task.Run(async () => { $statement })"
                : "System.Threading.Tasks.Task.Run(() => { $statement })",
            ("statement", new[] { statement })));
        var wait = awaits ? "await System.Threading.Tasks.Task.WhenAll($tasks);" : "System.Threading.Tasks.Task.WaitAll($tasks);";
        return [p.Template.Statement(wait, ("tasks", tasks.ToList()))];
    }
}

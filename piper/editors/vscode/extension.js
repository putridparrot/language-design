// Starts the Piper language server (`piper lsp`) for .piper files, and runs programs with `piper run`.
const fs = require("fs");
const path = require("path");
const vscode = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");

let client;

// The folder whose .piper files form the program containing `file`, as the language server decides: the nearest one
// above it with a piper.json, or a .csproj using Piper.Build. Null if the file is a program on its own.
function programRoot(file) {
    for (let dir = path.dirname(file); ; dir = path.dirname(dir)) {
        try {
            const entries = fs.readdirSync(dir);
            if (entries.includes("piper.json") ||
                entries.some(e => e.endsWith(".csproj") && fs.readFileSync(path.join(dir, e), "utf8").includes("Piper.Build"))) {
                return dir;
            }
        } catch {
            // An unreadable folder: keep looking above it.
        }
        if (path.dirname(dir) === dir) {
            return null;
        }
    }
}

// Runs the current file's program with `piper run` (the same command as the server, with `run` instead of `lsp`).
async function run() {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.languageId !== "piper") {
        vscode.window.showWarningMessage("Open a .piper file to run it.");
        return;
    }
    await vscode.workspace.saveAll(false);
    const file = editor.document.uri.fsPath;
    const target = programRoot(file) ?? file;

    const piper = piperCommand();
    if (!piper) {
        return;
    }
    const execution = new vscode.ShellExecution(piper.command, [...piper.args, "run", target], { cwd: path.dirname(target) });
    const task = new vscode.Task({ type: "piper", target }, vscode.TaskScope.Workspace, `run ${path.basename(target)}`, "piper", execution);
    task.presentationOptions = { reveal: vscode.TaskRevealKind.Always, clear: true, focus: false };
    await vscode.tasks.executeTask(task);
}

// True if `name` is an executable on the PATH (with any of Windows' executable extensions).
function onPath(name) {
    const extensions = process.platform === "win32" ? (process.env.PATHEXT ?? ".EXE;.CMD;.BAT").split(";") : [""];
    return (process.env.PATH ?? "").split(path.delimiter).some(dir =>
        extensions.some(ext => dir && fs.existsSync(path.join(dir, name + ext))));
}

// The compiler built in this repository, when the extension is run from editors/vscode.
const repoPiperDll = ["Debug", "Release"]
    .map(configuration => path.resolve(__dirname, "..", "..", "src", "Piper.Cli", "bin", configuration, "net10.0", "piper.dll"))
    .find(dll => fs.existsSync(dll));

// How to start the Piper tool, without its subcommand: the configured command, or, if that's the default `piper` and
// it isn't installed, `dotnet` with the piper.dll built in this repository. Null (after saying why) if neither exists.
function piperCommand() {
    const config = vscode.workspace.getConfiguration("piper");
    const command = config.get("server.path", "piper");
    const args = config.get("server.args", ["lsp"]).filter(a => a !== "lsp");
    if (command !== "piper" || onPath("piper")) {
        return { command, args };
    }
    if (repoPiperDll) {
        return { command: "dotnet", args: [repoPiperDll] };
    }
    vscode.window.showErrorMessage(
        "The `piper` command wasn't found. Install it with `dotnet tool install -g Piper --prerelease`, " +
        "build the compiler in this repository (`dotnet build`), or set `piper.server.path`.");
    return null;
}

function createClient() {
    const piper = piperCommand();
    if (!piper) {
        return null;
    }
    const server = { command: piper.command, args: [...piper.args, "lsp"], transport: TransportKind.stdio };
    return new LanguageClient(
        "piper",
        "Piper",
        { run: server, debug: server },
        {
            documentSelector: [{ scheme: "file", language: "piper" }],
            synchronize: { fileEvents: vscode.workspace.createFileSystemWatcher("**/*.piper") },
        });
}

async function start() {
    client = createClient();
    if (!client) {
        return;
    }
    try {
        await client.start();
    } catch (error) {
        vscode.window.showErrorMessage(
            `Couldn't start the Piper language server (${error.message ?? error}). ` +
            "Install it with `dotnet tool install -g Piper`, or set `piper.server.path`.");
    }
}

async function activate(context) {
    context.subscriptions.push(vscode.commands.registerCommand("piper.run", run));
    context.subscriptions.push(vscode.commands.registerCommand("piper.restartServer", async () => {
        if (client) {
            await client.stop();
        }
        await start();
    }));
    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(async e => {
        if (e.affectsConfiguration("piper.server")) {
            await vscode.commands.executeCommand("piper.restartServer");
        }
    }));
    await start();
}

async function deactivate() {
    if (client) {
        await client.stop();
    }
}

module.exports = { activate, deactivate };

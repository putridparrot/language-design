// Starts the Piper language server (`piper lsp`) for .piper files, runs programs with `piper run`, and debugs them.
const childProcess = require("child_process");
const crypto = require("crypto");
const fs = require("fs");
const os = require("os");
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

// Debugging: a `piper` launch configuration builds the program with `piper build --exe` and debugs the result with the
// C# extension's `coreclr` debugger, which binds breakpoints in .piper files through the PDB's `#line` mappings.
const output = vscode.window.createOutputChannel("Piper");

// The program to debug, from a launch configuration's `program` (a .piper file or a folder; by default the current
// file), expanding the variables VS Code would, since configurations are resolved here before VS Code expands them.
function debugTarget(folder, config) {
    const file = vscode.window.activeTextEditor?.document.uri.fsPath ?? "";
    const workspace = folder?.uri.fsPath ?? vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? "";
    const program = (config.program ?? "${file}")
        .replace(/\$\{file\}/g, file)
        .replace(/\$\{fileDirname\}/g, path.dirname(file))
        .replace(/\$\{workspaceFolder\}/g, workspace);
    const target = path.resolve(workspace, program);
    return fs.existsSync(target) && fs.statSync(target).isFile() && target.endsWith(".piper") ? programRoot(target) ?? target : target;
}

// Builds `target` into a folder under the system's temp folder, resolving to the path of its DLL, or null if the build
// failed (after showing why).
function buildForDebugging(piper, target) {
    const name = path.basename(target, ".piper");
    const hash = crypto.createHash("sha256").update(target.toLowerCase()).digest("hex").slice(0, 12);
    const directory = path.join(os.tmpdir(), "piper-debug", `${name}-${hash}`);
    const args = [...piper.args, "build", target, "--exe", "-o", directory, "--name", name];
    return new Promise(resolve => {
        childProcess.execFile(piper.command, args, { cwd: path.dirname(target) }, (error, stdout, stderr) => {
            if (!error) {
                resolve(path.join(directory, name + ".dll"));
                return;
            }
            output.clear();
            output.appendLine(`> ${piper.command} ${args.join(" ")}`);
            output.append(stderr || stdout || String(error));
            output.show(true);
            vscode.window.showErrorMessage(`Couldn't build ${path.basename(target)} for debugging; see the Piper output for why.`);
            resolve(null);
        });
    });
}

const debugConfigurationProvider = {
    async resolveDebugConfiguration(folder, config) {
        // F5 with no launch.json: debug the current file's program.
        if (!config.type && !config.request && !config.name) {
            if (vscode.window.activeTextEditor?.document.languageId !== "piper") {
                vscode.window.showWarningMessage("Open a .piper file to debug it.");
                return undefined;
            }
            config = { type: "piper", request: "launch", name: "Debug Piper program" };
        }
        if (!vscode.extensions.getExtension("ms-dotnettools.csharp")) {
            vscode.window.showErrorMessage("Debugging Piper needs Microsoft's C# extension (ms-dotnettools.csharp) for its .NET debugger.");
            return undefined;
        }
        const target = debugTarget(folder, config);
        if (!fs.existsSync(target)) {
            vscode.window.showErrorMessage(`There's no Piper program at ${target}.`);
            return undefined;
        }
        const piper = piperCommand();
        if (!piper) {
            return undefined;
        }
        await vscode.workspace.saveAll(false);
        const dll = await buildForDebugging(piper, target);
        if (!dll) {
            return undefined;
        }
        return {
            type: "coreclr",
            request: "launch",
            name: config.name,
            program: dll,
            args: config.args ?? [],
            cwd: config.cwd ?? path.dirname(target),
            env: config.env ?? {},
            console: config.console ?? "internalConsole",
            // The PDB has no checksums for .piper files, which `#line` can't give, so the debugger can't check them.
            requireExactSource: false,
        };
    },
};

// Debugs the current file's program, as F5 does without a launch.json.
async function debug() {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.languageId !== "piper") {
        vscode.window.showWarningMessage("Open a .piper file to debug it.");
        return;
    }
    const folder = vscode.workspace.getWorkspaceFolder(editor.document.uri);
    await vscode.debug.startDebugging(folder, { type: "piper", request: "launch", name: "Debug Piper program", program: editor.document.uri.fsPath });
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
        "The `piper` command wasn't found. Install it with `dotnet tool install -g Piper`, " +
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
            synchronize: { fileEvents: vscode.workspace.createFileSystemWatcher("**/{*.piper,piper.json}") },
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
    context.subscriptions.push(vscode.commands.registerCommand("piper.debug", debug));
    context.subscriptions.push(vscode.debug.registerDebugConfigurationProvider("piper", debugConfigurationProvider));
    context.subscriptions.push(output);
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

// Starts the Piper language server (`piper lsp`) for .piper files.
const vscode = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");

let client;

function createClient() {
    const config = vscode.workspace.getConfiguration("piper");
    const command = config.get("server.path", "piper");
    const args = config.get("server.args", ["lsp"]);
    const server = { command, args, transport: TransportKind.stdio };
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
    try {
        await client.start();
    } catch (error) {
        vscode.window.showErrorMessage(
            `Couldn't start the Piper language server (${error.message ?? error}). ` +
            "Install it with `dotnet tool install -g Piper`, or set `piper.server.path`.");
    }
}

async function activate(context) {
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

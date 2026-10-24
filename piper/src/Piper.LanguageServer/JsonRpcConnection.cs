using System.Text;
using System.Text.Json.Nodes;

namespace Piper.LanguageServer;

/// <summary>
/// JSON-RPC over a byte stream, framed as LSP requires: a <c>Content-Length</c> header, a blank line, then that many
/// bytes of UTF-8 JSON.
/// </summary>
public sealed class JsonRpcConnection(Stream input, Stream output)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>The next message, or null at the end of the input.</summary>
    public async Task<JsonObject?> ReadAsync(CancellationToken cancellation = default)
    {
        int? length = null;
        while (true)
        {
            var line = await ReadHeaderLineAsync(cancellation);
            if (line is null)
                return null;
            if (line.Length == 0)
                break;
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line[(colon + 1)..].Trim());
        }
        if (length is not { } count)
            throw new InvalidDataException("A message had no Content-Length header.");

        var buffer = new byte[count];
        await input.ReadExactlyAsync(buffer, cancellation);
        return JsonNode.Parse(buffer) as JsonObject;
    }

    /// <summary>Reads up to CRLF; headers are ASCII.</summary>
    private async Task<string?> ReadHeaderLineAsync(CancellationToken cancellation)
    {
        var builder = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(one, cancellation) == 0)
                return builder.Length == 0 ? null : builder.ToString();
            var c = (char)one[0];
            if (c == '\n')
                return builder.ToString().TrimEnd('\r');
            builder.Append(c);
        }
    }

    public async Task WriteAsync(JsonObject message, CancellationToken cancellation = default)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync(cancellation);
        try
        {
            await output.WriteAsync(header, cancellation);
            await output.WriteAsync(body, cancellation);
            await output.FlushAsync(cancellation);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}

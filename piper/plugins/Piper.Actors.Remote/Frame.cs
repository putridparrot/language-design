using System.Text;

namespace Piper.Actors.Remote;

/// <summary>What a frame between nodes is.</summary>
internal enum FrameKind : byte
{
    /// <summary>A message for an actor, whose sender carries on.</summary>
    Tell = 1,

    /// <summary>A message for an actor, whose sender waits for the answer (by the frame's id).</summary>
    Ask = 2,

    /// <summary>An ask's answer.</summary>
    Reply = 3,

    /// <summary>An ask's handler threw: the payload is its message.</summary>
    Fault = 4,

    /// <summary>An ask, or a stop, for an actor that isn't there.</summary>
    DeadLetter = 5,

    /// <summary>Stop the actor, and say when it has.</summary>
    Stop = 6,

    /// <summary>The actor a stop was for has stopped.</summary>
    Stopped = 7,
}

/// <summary>
/// One frame between nodes: what it is, the ask it belongs to, the node it's from (where answers go), the actor it's
/// for, its headers (a trace's context) and its payload (a message or an answer, as JSON). Encoded as a version byte, then
/// the fields in order, strings and lengths as .NET's BinaryWriter writes them.
/// </summary>
internal sealed record Frame(FrameKind Kind, long Id, string From, string To, IReadOnlyDictionary<string, string>? Headers, ReadOnlyMemory<byte> Payload)
{
    private const byte Version = 1;

    public byte[] Encode()
    {
        using var stream = new MemoryStream(64 + Payload.Length);
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write((byte)Kind);
            writer.Write(Id);
            writer.Write(From);
            writer.Write(To);
            writer.Write7BitEncodedInt(Headers?.Count ?? 0);
            foreach (var (key, value) in Headers ?? new Dictionary<string, string>())
            {
                writer.Write(key);
                writer.Write(value);
            }
            writer.Write7BitEncodedInt(Payload.Length);
            writer.Write(Payload.Span);
        }
        return stream.ToArray();
    }

    public static Frame Decode(ReadOnlyMemory<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadByte() is var version && version != Version)
            throw new InvalidDataException($"The frame is version {version}, which this node doesn't read (it reads {Version}).");
        var kind = (FrameKind)reader.ReadByte();
        var id = reader.ReadInt64();
        var from = reader.ReadString();
        var to = reader.ReadString();
        var count = reader.Read7BitEncodedInt();
        Dictionary<string, string>? headers = count == 0 ? null : new(count);
        for (var i = 0; i < count; i++)
            headers![reader.ReadString()] = reader.ReadString();
        var payload = reader.ReadBytes(reader.Read7BitEncodedInt());
        return new Frame(kind, id, from, to, headers, payload);
    }
}

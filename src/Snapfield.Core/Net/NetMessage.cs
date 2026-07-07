using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snapfield.Core.Persistence;

namespace Snapfield.Core.Net;

public enum MsgType
{
    Hello,        // exchange machine id + monitors on connect
    CursorMove,   // warp receiver cursor to (X, Y) in its virtual-desktop pixels
    MouseButton,  // Button (0=L,1=R,2=M), Down
    MouseWheel,   // WheelDelta, Horizontal
    ControlEnter, // control just entered the receiver
    ControlLeave, // control just left the receiver
    Key,          // Vk, Scan, Down, Extended — keyboard transition
    Clipboard,      // Text — clipboard text changed on the sender
    AuthFail,       // receiver rejected the controller's pairing pin
    ClipboardImage, // Text carries base64 PNG — clipboard image changed on the sender
    Layout,         // Monitors — the controller's combined global plane, for the receiver to display
    ClipboardFiles, // Files — copied files (name + base64 bytes) shared via the clipboard
}

/// <summary>One file shared over the clipboard (name + base64-encoded content).</summary>
public sealed record SharedFile
{
    public string Name { get; init; } = "";
    public string Data { get; init; } = ""; // base64
}

/// <summary>
/// One wire message. A single flat record keeps (de)serialisation trivial; unused
/// fields stay at their defaults per message type. Frames are length-prefixed
/// (4-byte little-endian length + UTF-8 JSON body).
/// </summary>
public sealed record NetMessage
{
    public MsgType Type { get; init; }
    public string? MachineId { get; init; }
    public MonitorState[]? Monitors { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Button { get; init; }
    public bool Down { get; init; }
    public int WheelDelta { get; init; }
    public bool Horizontal { get; init; }
    public int Vk { get; init; }
    public int Scan { get; init; }
    public bool Extended { get; init; }
    public string? Text { get; init; }
    public string? Pin { get; init; }
    public SharedFile[]? Files { get; init; }

    public static NetMessage Hello(string machineId, MonitorState[] monitors, string? pin = null) =>
        new() { Type = MsgType.Hello, MachineId = machineId, Monitors = monitors, Pin = pin };

    public static NetMessage ClipboardText(string text) => new() { Type = MsgType.Clipboard, Text = text };
    public static NetMessage ClipboardPng(byte[] png) => new() { Type = MsgType.ClipboardImage, Text = Convert.ToBase64String(png) };
    public static NetMessage AuthFailed() => new() { Type = MsgType.AuthFail };
    public static NetMessage LayoutSync(MonitorState[] monitors) => new() { Type = MsgType.Layout, Monitors = monitors };
    public static NetMessage ClipboardFilesMsg(SharedFile[] files) => new() { Type = MsgType.ClipboardFiles, Files = files };

    public static NetMessage Cursor(int x, int y) => new() { Type = MsgType.CursorMove, X = x, Y = y };
    public static NetMessage MouseBtn(int button, bool down) => new() { Type = MsgType.MouseButton, Button = button, Down = down };
    public static NetMessage Wheel(int delta, bool horizontal) => new() { Type = MsgType.MouseWheel, WheelDelta = delta, Horizontal = horizontal };
    public static NetMessage KeyEvent(int vk, int scan, bool down, bool extended) =>
        new() { Type = MsgType.Key, Vk = vk, Scan = scan, Down = down, Extended = extended };
    public static NetMessage Enter() => new() { Type = MsgType.ControlEnter };
    public static NetMessage Leave() => new() { Type = MsgType.ControlLeave };

    // ── binary fast-path ─────────────────────────────────────────────────────
    // Messages that flow at input rate (cursor at mouse-polling frequency, plus
    // buttons/wheel/keys) skip JSON entirely: a fixed little-endian layout
    // tagged by a first byte that can never start a JSON body ('{' = 0x7B), so
    // both encodings share the wire and FromBody picks by that byte.

    /// <summary>Upper bound of any binary-encoded body (see <see cref="TryEncodeBinary"/>).</summary>
    public const int MaxBinaryLength = 16;

    private const byte BinCursor = 0x01; // [tag][x:i32][y:i32]                 = 9
    private const byte BinButton = 0x02; // [tag][button:u8][down:u8]           = 3
    private const byte BinWheel  = 0x03; // [tag][delta:i32][horizontal:u8]     = 6
    private const byte BinKey    = 0x04; // [tag][vk:i32][scan:i32][flags:u8]   = 10

    /// <summary>Encodes a high-rate message into its fixed binary form. Returns
    /// false for message types that go as JSON. <paramref name="dest"/> must be
    /// at least <see cref="MaxBinaryLength"/> bytes.</summary>
    public bool TryEncodeBinary(Span<byte> dest, out int written)
    {
        switch (Type)
        {
            case MsgType.CursorMove:
                dest[0] = BinCursor;
                BinaryPrimitives.WriteInt32LittleEndian(dest[1..], X);
                BinaryPrimitives.WriteInt32LittleEndian(dest[5..], Y);
                written = 9;
                return true;
            case MsgType.MouseButton:
                dest[0] = BinButton;
                dest[1] = (byte)Button;
                dest[2] = Down ? (byte)1 : (byte)0;
                written = 3;
                return true;
            case MsgType.MouseWheel:
                dest[0] = BinWheel;
                BinaryPrimitives.WriteInt32LittleEndian(dest[1..], WheelDelta);
                dest[5] = Horizontal ? (byte)1 : (byte)0;
                written = 6;
                return true;
            case MsgType.Key:
                dest[0] = BinKey;
                BinaryPrimitives.WriteInt32LittleEndian(dest[1..], Vk);
                BinaryPrimitives.WriteInt32LittleEndian(dest[5..], Scan);
                dest[9] = (byte)((Down ? 1 : 0) | (Extended ? 2 : 0));
                written = 10;
                return true;
            default:
                written = 0;
                return false;
        }
    }

    /// <summary>Serialise the message body to UTF-8 JSON (no length prefix).</summary>
    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, NetMessageJsonContext.Default.NetMessage);

    /// <summary>Serialise into a caller-owned writer — lets the send loop reuse
    /// its buffers instead of allocating per message.</summary>
    public void WriteTo(Utf8JsonWriter writer) =>
        JsonSerializer.Serialize(writer, this, NetMessageJsonContext.Default.NetMessage);

    /// <summary>Decodes a message body: binary fast-path frames by their tag
    /// byte, everything else as JSON. Null for malformed/unknown bodies.</summary>
    public static NetMessage? FromBody(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0) return null;
        switch (body[0])
        {
            case BinCursor:
                if (body.Length != 9) return null;
                return Cursor(
                    BinaryPrimitives.ReadInt32LittleEndian(body[1..]),
                    BinaryPrimitives.ReadInt32LittleEndian(body[5..]));
            case BinButton:
                if (body.Length != 3) return null;
                return MouseBtn(body[1], body[2] != 0);
            case BinWheel:
                if (body.Length != 6) return null;
                return Wheel(BinaryPrimitives.ReadInt32LittleEndian(body[1..]), body[5] != 0);
            case BinKey:
                if (body.Length != 10) return null;
                return KeyEvent(
                    BinaryPrimitives.ReadInt32LittleEndian(body[1..]),
                    BinaryPrimitives.ReadInt32LittleEndian(body[5..]),
                    (body[9] & 1) != 0,
                    (body[9] & 2) != 0);
            default:
                try { return JsonSerializer.Deserialize(body, NetMessageJsonContext.Default.NetMessage); }
                catch (JsonException) { return null; }
        }
    }
}

// Omit default-valued fields: a JSON-encoded message carries only the fields it
// uses instead of all 15. Deserialising fills missing fields with the same
// defaults, so the round-trip is lossless. Source-generated: no reflection on
// the per-message path, and trimming/AOT safe.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault)]
[JsonSerializable(typeof(NetMessage))]
internal sealed partial class NetMessageJsonContext : JsonSerializerContext;

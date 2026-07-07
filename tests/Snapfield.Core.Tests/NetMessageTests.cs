using Snapfield.Core.Net;
using Snapfield.Core.Persistence;
using Xunit;

namespace Snapfield.Core.Tests;

public class NetMessageTests
{
    private static NetMessage RoundTripBinary(NetMessage msg)
    {
        var buf = new byte[NetMessage.MaxBinaryLength];
        Assert.True(msg.TryEncodeBinary(buf, out var len));
        Assert.InRange(len, 1, NetMessage.MaxBinaryLength);
        var decoded = NetMessage.FromBody(buf.AsSpan(0, len));
        Assert.NotNull(decoded);
        return decoded!;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3840, 2160)]
    [InlineData(-1920, -480)] // virtual desktop coordinates can be negative
    public void Cursor_RoundTripsBinary(int x, int y)
    {
        var m = RoundTripBinary(NetMessage.Cursor(x, y));
        Assert.Equal(MsgType.CursorMove, m.Type);
        Assert.Equal(x, m.X);
        Assert.Equal(y, m.Y);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void MouseButton_RoundTripsBinary(int button, bool down)
    {
        var m = RoundTripBinary(NetMessage.MouseBtn(button, down));
        Assert.Equal(MsgType.MouseButton, m.Type);
        Assert.Equal(button, m.Button);
        Assert.Equal(down, m.Down);
    }

    [Theory]
    [InlineData(120, false)]
    [InlineData(-120, true)]
    public void Wheel_RoundTripsBinary(int delta, bool horizontal)
    {
        var m = RoundTripBinary(NetMessage.Wheel(delta, horizontal));
        Assert.Equal(MsgType.MouseWheel, m.Type);
        Assert.Equal(delta, m.WheelDelta);
        Assert.Equal(horizontal, m.Horizontal);
    }

    [Theory]
    [InlineData(0x41, 30, true, false)]
    [InlineData(0xA3, 285, false, true)] // right ctrl: extended key
    public void Key_RoundTripsBinary(int vk, int scan, bool down, bool extended)
    {
        var m = RoundTripBinary(NetMessage.KeyEvent(vk, scan, down, extended));
        Assert.Equal(MsgType.Key, m.Type);
        Assert.Equal(vk, m.Vk);
        Assert.Equal(scan, m.Scan);
        Assert.Equal(down, m.Down);
        Assert.Equal(extended, m.Extended);
    }

    [Fact]
    public void LowRateMessages_DeclineBinary_AndRoundTripJson()
    {
        var monitors = new[]
        {
            new MonitorState { MachineId = "PC1", DeviceId = "MON-A", PixelWidth = 1920, PixelHeight = 1080 },
        };
        var msg = NetMessage.Hello("PC1", monitors, pin: "1234");

        Assert.False(msg.TryEncodeBinary(new byte[NetMessage.MaxBinaryLength], out _));

        var decoded = NetMessage.FromBody(msg.ToJson());
        Assert.NotNull(decoded);
        Assert.Equal(MsgType.Hello, decoded!.Type);
        Assert.Equal("PC1", decoded.MachineId);
        Assert.Equal("1234", decoded.Pin);
        Assert.Single(decoded.Monitors!);
        Assert.Equal("MON-A", decoded.Monitors![0].DeviceId);
    }

    [Fact]
    public void Json_OmitsDefaultFields()
    {
        // Wire-size regression guard: a cursor message encoded as JSON must not
        // carry the 12 unused fields.
        var json = System.Text.Encoding.UTF8.GetString(NetMessage.Cursor(10, 20).ToJson());
        Assert.DoesNotContain("MachineId", json);
        Assert.DoesNotContain("Vk", json);
    }

    [Fact]
    public void FromBody_JsonBody_StillDecodes()
    {
        // Peers that predate the binary fast-path send every message as JSON —
        // the decoder must keep accepting those frames.
        var body = System.Text.Encoding.UTF8.GetBytes("""{"Type":1,"X":100,"Y":200}""");
        var m = NetMessage.FromBody(body);
        Assert.NotNull(m);
        Assert.Equal(MsgType.CursorMove, m!.Type);
        Assert.Equal(100, m.X);
        Assert.Equal(200, m.Y);
    }

    [Fact]
    public void SwitchFeedbackMessages_RoundTripJson()
    {
        var ping = NetMessage.FromBody(NetMessage.Ping(640, 512).ToJson());
        Assert.Equal(MsgType.CursorPing, ping!.Type);
        Assert.Equal(640, ping.X);
        Assert.Equal(512, ping.Y);

        var show = NetMessage.FromBody(NetMessage.SwitcherShowMsg("PC-1\nPC-2\n노트북", 2).ToJson());
        Assert.Equal(MsgType.SwitcherShow, show!.Type);
        Assert.Equal(new[] { "PC-1", "PC-2", "노트북" }, show.Text!.Split('\n'));
        Assert.Equal(2, show.X);

        Assert.Equal(MsgType.SwitcherHide, NetMessage.FromBody(NetMessage.SwitcherHideMsg().ToJson())!.Type);
    }

    [Fact]
    public void MsgTypeValues_AreWireStable()
    {
        // MsgType goes over the wire as its number — reordering the enum breaks
        // every mixed-version pair. New members append only.
        Assert.Equal(11, (int)MsgType.ClipboardFiles);
        Assert.Equal(12, (int)MsgType.CursorPing);
        Assert.Equal(13, (int)MsgType.SwitcherShow);
        Assert.Equal(14, (int)MsgType.SwitcherHide);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x01, 0x00 })]       // truncated cursor frame
    [InlineData(new byte[] { 0x7F, 0x00, 0x00 })] // unknown tag, not JSON
    [InlineData(new byte[] { (byte)'{', (byte)'x' })] // malformed JSON
    public void FromBody_MalformedBody_ReturnsNull(byte[] body)
    {
        Assert.Null(NetMessage.FromBody(body));
    }
}

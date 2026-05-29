using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Round-trip and format tests for the hand-rolled OSC encoder/decoder.
/// </summary>
public class OscMessageTests
{
    [Fact]
    public void Float_argument_round_trips()
    {
        var message = new OscMessage("/ch/01/mix/fader", ",f", new List<object> { 0.75f });

        OscMessage decoded = OscMessage.Decode(message.Encode());

        Assert.Equal("/ch/01/mix/fader", decoded.Address);
        Assert.Equal(",f", decoded.TypeTags);
        Assert.Equal(0.75f, (float)decoded.Arguments[0]);
    }

    [Fact]
    public void Int_argument_round_trips()
    {
        var message = new OscMessage("/renew", ",i", new List<object> { 12345 });

        OscMessage decoded = OscMessage.Decode(message.Encode());

        Assert.Equal(12345, (int)decoded.Arguments[0]);
    }

    [Fact]
    public void String_argument_round_trips()
    {
        var message = new OscMessage("/unsubscribe", ",s", new List<object> { "/m32lct_ch05" });

        OscMessage decoded = OscMessage.Decode(message.Encode());

        Assert.Equal("/m32lct_ch05", (string)decoded.Arguments[0]);
    }

    [Fact]
    public void Blob_argument_round_trips()
    {
        byte[] blob = { 1, 2, 3, 4, 5 };
        var message = new OscMessage("/meters/2", ",b", new List<object> { blob });

        OscMessage decoded = OscMessage.Decode(message.Encode());

        Assert.Equal(blob, (byte[])decoded.Arguments[0]);
    }

    [Fact]
    public void Mixed_subscribe_arguments_round_trip()
    {
        // This is the exact shape of a /batchsubscribe message.
        var message = new OscMessage(
            "/batchsubscribe",
            ",ssiii",
            new List<object> { "/m32lct_ch01", "/meters/6", 0, 0, 1 });

        OscMessage decoded = OscMessage.Decode(message.Encode());

        Assert.Equal("/batchsubscribe", decoded.Address);
        Assert.Equal("/m32lct_ch01", (string)decoded.Arguments[0]);
        Assert.Equal("/meters/6", (string)decoded.Arguments[1]);
        Assert.Equal(0, (int)decoded.Arguments[2]);
        Assert.Equal(0, (int)decoded.Arguments[3]);
        Assert.Equal(1, (int)decoded.Arguments[4]);
    }

    [Fact]
    public void Encoded_length_is_a_multiple_of_four()
    {
        // OSC pads every block to a 4-byte boundary, so the total is a multiple of 4.
        var message = new OscMessage("/ch/01/mix/fader", ",f", new List<object> { 0.5f });

        Assert.Equal(0, message.Encode().Length % 4);
    }

    [Fact]
    public void Int_is_encoded_big_endian()
    {
        // Address "/i" + tags ",i" both pad to 4 bytes, so the int 1 lands in the
        // last 4 bytes as 0x00 0x00 0x00 0x01 (big-endian).
        var message = new OscMessage("/i", ",i", new List<object> { 1 });

        byte[] bytes = message.Encode();

        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01 }, bytes[^4..]);
    }

    [Fact]
    public void Empty_blob_round_trips()
    {
        var message = new OscMessage("/meters/2", ",b", new List<object> { Array.Empty<byte>() });

        OscMessage decoded = OscMessage.Decode(message.Encode());

        Assert.Empty((byte[])decoded.Arguments[0]);
    }

    [Fact]
    public void Decode_rejects_blob_with_corrupt_length()
    {
        // "/x\0\0" + ",b\0\0" + a 4-byte big-endian length of 0x7FFFFFFF, with no
        // payload following. A naive decoder would try to allocate ~2 GB from this
        // one stray packet; ours must reject it instead of crashing or thrashing.
        byte[] data =
        {
            0x2F, 0x78, 0x00, 0x00,   // "/x"
            0x2C, 0x62, 0x00, 0x00,   // ",b"
            0x7F, 0xFF, 0xFF, 0xFF    // declared blob length = 2147483647
        };

        Assert.Throws<FormatException>(() => OscMessage.Decode(data));
    }
}

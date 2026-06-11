using M32LiveConsoleTool.Domain;
using M32LiveConsoleTool.Modules;
using M32LiveConsoleTool.Osc;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Tests for <see cref="MeterReader"/> helpers that do not need a live socket.
/// </summary>
public class MeterReaderTests
{
    [Fact]
    public void GetMovingAverage_averages_in_the_power_domain()
    {
        // The OscClient is never connected; GetMovingAverage only reads the device's
        // in-memory sample history, so no socket is opened.
        var client = new OscClient("127.0.0.1", 17801, 17802);
        var channel = new MixerChannel("X", 1, -18.0, 0.0);
        var meter = new MeterReader(client, new List<AudioDevice> { channel }, windowMs: 5000);

        // One loud sample (-6 dBFS: amplitude 10^(-6/20) ~ 0.501) and one near-silent
        // one (~ -80 dBFS).
        channel.AddSample(new MeterSample(0.501f));
        channel.AddSample(new MeterSample(0.0001f));

        double avg = meter.GetMovingAverage(channel);

        // Power average ~ 10*log10((0.501^2 + 0.0001^2)/2) ~ -9 dB. A dB-domain
        // average would be (-6 + -80)/2 = -43 dB, so a value well above -20 confirms
        // the power-domain behavior; it must also stay below the loud sample.
        Assert.True(avg > -20.0, $"expected a power-domain average near -9 dB, got {avg:F1}");
        Assert.True(avg < -3.0, $"average should stay below the loud sample, got {avg:F1}");
    }

    [Fact]
    public void GetMovingAverage_returns_floor_when_no_samples()
    {
        var client = new OscClient("127.0.0.1", 17803, 17804);
        var channel = new MixerChannel("X", 1, -18.0, 0.0);
        var meter = new MeterReader(client, new List<AudioDevice> { channel }, windowMs: 5000);

        Assert.Equal(-90.0, meter.GetMovingAverage(channel));
    }

    // ─────────────────────────────────────────────────────────────────────
    // Meter-blob parsing (M32 native format: little-endian count + floats)
    // ─────────────────────────────────────────────────────────────────────

    private static byte[] BuildMeterBlob(int declaredCount, params float[] floats)
    {
        byte[] blob = new byte[4 + floats.Length * 4];
        Buffer.BlockCopy(BitConverter.GetBytes(declaredCount), 0, blob, 0, 4);
        for (int i = 0; i < floats.Length; i++)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(floats[i]), 0, blob, 4 + i * 4, 4);
        }
        return blob;
    }

    [Fact]
    public void ParseMeterBlob_decodes_a_valid_blob()
    {
        byte[] blob = BuildMeterBlob(2, 0.5f, 1.0f);

        float[] meters = MeterReader.ParseMeterBlob(blob);

        Assert.Equal(new[] { 0.5f, 1.0f }, meters);
    }

    [Fact]
    public void ParseMeterBlob_rejects_a_truncated_blob()
    {
        Assert.Empty(MeterReader.ParseMeterBlob(new byte[] { 0x01, 0x00 }));
        Assert.Empty(MeterReader.ParseMeterBlob(Array.Empty<byte>()));
    }

    [Fact]
    public void ParseMeterBlob_rejects_a_negative_count()
    {
        byte[] blob = BuildMeterBlob(-1, 0.5f);

        Assert.Empty(MeterReader.ParseMeterBlob(blob));
    }

    [Fact]
    public void ParseMeterBlob_rejects_a_count_exceeding_the_payload()
    {
        // Declares 100 floats but carries only 2. A corrupt packet must yield
        // nothing rather than read past the buffer or allocate from a lie.
        byte[] blob = BuildMeterBlob(100, 0.5f, 1.0f);

        Assert.Empty(MeterReader.ParseMeterBlob(blob));
    }

    [Fact]
    public void ParseMeterBlob_rejects_a_huge_count_that_would_overflow()
    {
        byte[] blob = BuildMeterBlob(int.MaxValue);

        Assert.Empty(MeterReader.ParseMeterBlob(blob));
    }
}

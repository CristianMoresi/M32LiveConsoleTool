using M32LiveConsoleTool.Domain;

namespace M32LiveConsoleTool.Tests;

/// <summary>
/// Deterministic tests for the manual-fader-change detection logic, which
/// depends on the console reporting fader positions back to the app.
/// </summary>
public class FaderFeedbackTests
{
    [Fact]
    public void First_console_report_seeds_last_sent_and_marks_initialized()
    {
        var channel = new MixerChannel("Vox", 1, -18.0, 0.0);
        Assert.False(channel.FaderInitialized);

        channel.UpdateFaderFromConsole(0.5f);

        Assert.True(channel.FaderInitialized);
        Assert.Equal(0.5f, channel.CurrentFader);
        Assert.Equal(0.5f, channel.LastSentFader); // seeded from the first report
        Assert.False(channel.DetectManualChange()); // current == lastSent
    }

    [Fact]
    public void External_fader_move_is_detected_as_manual_change()
    {
        var channel = new MixerChannel("Vox", 1, -18.0, 0.0);
        channel.UpdateFaderFromConsole(0.5f); // initial sync

        channel.UpdateFaderFromConsole(0.8f); // engineer moves the fader on the console

        Assert.Equal(0.8f, channel.CurrentFader);
        Assert.Equal(0.5f, channel.LastSentFader); // not re-seeded
        Assert.True(channel.DetectManualChange());
    }

    [Fact]
    public void Console_echo_of_our_own_write_is_not_a_manual_change()
    {
        var bus = new Bus("Drums", 3, -18.0);
        bus.UpdateFaderFromConsole(0.6f); // initial sync
        bus.LastSentFader = 0.7f;          // we send a correction
        bus.UpdateFaderFromConsole(0.7f);  // console echoes it back

        Assert.False(bus.DetectManualChange());
    }
}

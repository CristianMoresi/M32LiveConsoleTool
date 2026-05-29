namespace M32LiveConsoleTool.Domain;

/// <summary>
/// Serializable descriptor of an <see cref="AudioDevice"/>: holds only the data
/// needed to rebuild the device when loading a configuration, with no runtime
/// state (meter history, last reported fader, etc.).
/// </summary>
public class AudioDeviceDescriptor
{
    /// <summary>Device type: "channel" or "bus".</summary>
    public string Type { get; set; } = "channel";

    /// <summary>Human-readable device name.</summary>
    public string Name { get; set; } = "";

    /// <summary>OSC number (1-32 for channels, 1-16 for buses).</summary>
    public int OscNumber { get; set; }

    /// <summary>Initial target level in dB.</summary>
    public double TargetLevelDb { get; set; }

    /// <summary>Initial preamp gain in dB (channels only).</summary>
    public double PreampGain { get; set; }

    /// <summary>
    /// Rebuilds a concrete <see cref="AudioDevice"/> from this descriptor.
    /// </summary>
    public AudioDevice ToAudioDevice()
    {
        if (Type == "bus")
        {
            return new Bus(Name, OscNumber, TargetLevelDb);
        }

        return new MixerChannel(Name, OscNumber, TargetLevelDb, PreampGain);
    }

    /// <summary>
    /// Creates a descriptor from a live device.
    /// </summary>
    public static AudioDeviceDescriptor FromAudioDevice(AudioDevice device)
    {
        AudioDeviceDescriptor descriptor = new AudioDeviceDescriptor();
        descriptor.Name = device.Name;
        descriptor.OscNumber = device.OscNumber;
        descriptor.TargetLevelDb = device.TargetLevelDb;

        if (device is MixerChannel channel)
        {
            descriptor.Type = "channel";
            descriptor.PreampGain = channel.PreampGain;
        }
        else
        {
            descriptor.Type = "bus";
            descriptor.PreampGain = 0;
        }

        return descriptor;
    }
}

using System.Buffers.Binary;
using System.Text;

namespace M32LiveConsoleTool.Osc;

/// <summary>
/// A single OSC (Open Sound Control) message, ready to be sent to the M32 or
/// rebuilt from a received byte sequence.
/// </summary>
/// <remarks>
/// <para>
/// An OSC message is made of three parts serialized one after another:
/// </para>
/// <list type="bullet">
///   <item><term>Address</term><description>URL-like ASCII string (e.g. <c>/ch/01/mix/fader</c>).</description></item>
///   <item><term>Type tags</term><description>ASCII string starting with a comma that describes each argument's type (e.g. <c>,f</c>).</description></item>
///   <item><term>Arguments</term><description>Data in the order and type given by the type tags.</description></item>
/// </list>
/// <para>
/// Encoding rules:
/// </para>
/// <list type="bullet">
///   <item>Each block (address, type tags, each string/blob) is padded with null bytes up to a multiple of 4.</item>
///   <item>32-bit <c>int</c> and <c>float</c> are encoded <strong>big-endian</strong> (the M32 requires it; x86/x64 CPUs are little-endian, so the bytes must be reversed).</item>
///   <item>Strings are ASCII and always end with a <c>0x00</c> byte before the padding.</item>
/// </list>
/// </remarks>
public class OscMessage
{
    // ─────────────────────────────────────────────────────────────────────
    // Private fields
    // ─────────────────────────────────────────────────────────────────────

    private string address;
    private string typeTags;
    private List<object> arguments;

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new OSC message with the three given components.
    /// </summary>
    /// <param name="address">OSC address. Must start with '/' (e.g. "/ch/01/mix/fader").</param>
    /// <param name="typeTags">Type-tag string. Must start with ',' and contain one letter per argument.</param>
    /// <param name="arguments">Arguments in the same order as the type tags.</param>
    public OscMessage(string address, string typeTags, List<object> arguments)
    {
        this.address = address;
        this.typeTags = typeTags;
        this.arguments = arguments;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public properties
    // ─────────────────────────────────────────────────────────────────────

    public string Address
    {
        get { return address; }
        set { address = value; }
    }

    public string TypeTags
    {
        get { return typeTags; }
        set { typeTags = value; }
    }

    public List<object> Arguments
    {
        get { return arguments; }
        set { arguments = value; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public methods: serialization and deserialization
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes the message into a byte sequence ready to send over UDP.
    /// </summary>
    /// <returns>Byte array with the message encoded per OSC 1.0.</returns>
    public byte[] Encode()
    {
        if (!address.StartsWith('/') || !typeTags.StartsWith(',') || typeTags.Length - 1 != arguments.Count)
            throw new FormatException("OSC address, tags and argument count do not agree.");
        int size = PaddedStringSize(address) + PaddedStringSize(typeTags);
        foreach (object argument in arguments)
            size = checked(size + (argument is string text ? PaddedStringSize(text) : argument is byte[] blob ? 4 + Padded(blob.Length) : 4));
        byte[] data = new byte[size];
        int offset = 0;
        WriteString(address, data, ref offset);
        WriteString(typeTags, data, ref offset);
        for (int i = 0; i < arguments.Count; i++)
        {
            switch (typeTags[i + 1])
            {
                case 'i': BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset, 4), (int)arguments[i]); offset += 4; break;
                case 'f': BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset, 4), (float)arguments[i]); offset += 4; break;
                case 's': WriteString((string)arguments[i], data, ref offset); break;
                case 'b':
                    byte[] blob = (byte[])arguments[i];
                    BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset, 4), blob.Length); offset += 4;
                    blob.CopyTo(data, offset); offset += Padded(blob.Length); break;
                default: throw new NotSupportedException($"Unsupported OSC tag: {typeTags[i + 1]}");
            }
        }
        return data;
    }

    private static int Padded(int length) => checked((length + 3) / 4 * 4);
    private static int PaddedStringSize(string text) => Padded(Encoding.ASCII.GetByteCount(text) + 1);
    private static void WriteString(string text, byte[] data, ref int offset)
    {
        if (text.Contains('\0')) throw new FormatException("Embedded NUL in OSC string.");
        Encoding.ASCII.GetBytes(text, data.AsSpan(offset));
        offset += PaddedStringSize(text);
    }

    public static OscMessage Decode(byte[] data)
    {
        int offset = 0;
        string address = ReadString(data, ref offset);
        string tags = ReadString(data, ref offset);
        if (!address.StartsWith('/') || !tags.StartsWith(',')) throw new FormatException("Invalid OSC address or tags.");
        var args = new List<object>(tags.Length - 1);
        foreach (char tag in tags.AsSpan(1))
        {
            switch (tag)
            {
                case 'i': Require(data, offset, 4); args.Add(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4))); offset += 4; break;
                case 'f': Require(data, offset, 4); args.Add(BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(offset, 4))); offset += 4; break;
                case 's': args.Add(ReadString(data, ref offset)); break;
                case 'b':
                    Require(data, offset, 4);
                    int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4)); offset += 4;
                    Require(data, offset, length);
                    int padded = Padded(length); Require(data, offset, padded);
                    args.Add(data.AsSpan(offset, length).ToArray()); offset += padded; break;
                default: throw new NotSupportedException($"Unsupported OSC tag: {tag}");
            }
        }
        return new OscMessage(address, tags, args);
    }

    private static void Require(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || length > data.Length - offset) throw new FormatException("Truncated OSC packet.");
    }
    private static string ReadString(byte[] data, ref int offset)
    {
        Require(data, offset, 1);
        int end = Array.IndexOf(data, (byte)0, offset);
        if (end < 0) throw new FormatException("Unterminated OSC string.");
        int length = end - offset;
        int padded = Padded(length + 1); Require(data, offset, padded);
        string text = Encoding.ASCII.GetString(data, offset, length);
        offset += padded;
        return text;
    }
}

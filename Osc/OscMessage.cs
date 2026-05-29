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
        // Accumulate bytes in a temporary list and convert to an array at the
        // end. This is the most readable way to build the message.
        List<byte> result = new List<byte>();

        // Block 1: OSC address.
        result.AddRange(EncodeString(address));

        // Block 2: type tags.
        result.AddRange(EncodeString(typeTags));

        // Block 3: arguments. Walk the type tags, skipping the leading comma, and
        // take the matching argument for each letter.
        for (int i = 0; i < typeTags.Length - 1; i++)
        {
            char tag = typeTags[i + 1];        // skip the comma at index 0
            object arg = arguments[i];

            switch (tag)
            {
                case 'i':
                    result.AddRange(EncodeInt((int)arg));
                    break;
                case 'f':
                    result.AddRange(EncodeFloat((float)arg));
                    break;
                case 's':
                    result.AddRange(EncodeString((string)arg));
                    break;
                case 'b':
                    result.AddRange(EncodeBlob((byte[])arg));
                    break;
                default:
                    throw new NotSupportedException($"Unsupported type tag: '{tag}'");
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Rebuilds an OSC message from bytes received over UDP.
    /// </summary>
    /// <param name="data">Byte array received from the console.</param>
    /// <returns>A new <see cref="OscMessage"/> with the decoded content.</returns>
    public static OscMessage Decode(byte[] data)
    {
        int offset = 0;

        // Read the address and advance the cursor.
        string address = DecodeString(data, ref offset);

        // Read the type tags and advance the cursor.
        string typeTags = DecodeString(data, ref offset);

        // Read the arguments one by one, following the type tags.
        List<object> arguments = new List<object>();
        for (int i = 0; i < typeTags.Length - 1; i++)
        {
            char tag = typeTags[i + 1];

            switch (tag)
            {
                case 'i':
                    arguments.Add(DecodeInt(data, ref offset));
                    break;
                case 'f':
                    arguments.Add(DecodeFloat(data, ref offset));
                    break;
                case 's':
                    arguments.Add(DecodeString(data, ref offset));
                    break;
                case 'b':
                    arguments.Add(DecodeBlob(data, ref offset));
                    break;
                default:
                    throw new NotSupportedException($"Unsupported type tag: '{tag}'");
            }
        }

        return new OscMessage(address, typeTags, arguments);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private methods: encoding
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes a string as ASCII bytes, null-terminated and padded to a multiple of 4.
    /// </summary>
    private byte[] EncodeString(string s)
    {
        // ASCII because OSC requires it (and because M32 addresses are pure ASCII:
        // slashes, digits and standard letters).
        byte[] ascii = Encoding.ASCII.GetBytes(s);

        // Final length with the null terminator, padded to a multiple of 4.
        int totalLength = ascii.Length + 1;                       // +1 for the trailing 0x00
        int paddedLength = ((totalLength + 3) / 4) * 4;           // round up to a multiple of 4

        byte[] result = new byte[paddedLength];
        Array.Copy(ascii, result, ascii.Length);                  // ASCII bytes first
        // The remaining bytes are already 0 in C#: the null terminator and the
        // padding appear automatically when the array is initialized.

        return result;
    }

    /// <summary>
    /// Encodes a 32-bit int as big-endian (4 bytes).
    /// </summary>
    private byte[] EncodeInt(int i)
    {
        byte[] bytes = BitConverter.GetBytes(i);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    /// <summary>
    /// Encodes a 32-bit float as IEEE 754 big-endian (4 bytes).
    /// </summary>
    private byte[] EncodeFloat(float f)
    {
        byte[] bytes = BitConverter.GetBytes(f);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    /// <summary>
    /// Encodes a byte blob: 4-byte big-endian length followed by the content,
    /// padded to a multiple of 4.
    /// </summary>
    private byte[] EncodeBlob(byte[] blob)
    {
        byte[] lengthBytes = EncodeInt(blob.Length);
        int paddedDataLength = ((blob.Length + 3) / 4) * 4;

        byte[] result = new byte[4 + paddedDataLength];
        Array.Copy(lengthBytes, 0, result, 0, 4);
        Array.Copy(blob, 0, result, 4, blob.Length);
        // The trailing padding is already 0 by default.

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private methods: decoding
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a null-terminated ASCII string from the given position and advances
    /// the cursor to the next multiple of 4.
    /// </summary>
    private static string DecodeString(byte[] data, ref int offset)
    {
        // Find the first 0x00 byte from the current offset.
        int end = offset;

        while (end < data.Length && data[end] != 0)
        {
            end++;
        }

        string result = Encoding.ASCII.GetString(data, offset, end - offset);

        // Advance the cursor: count the null terminator and jump to the next
        // multiple of 4.
        int consumed = end - offset + 1;                      // +1 for the 0x00
        int padded = ((consumed + 3) / 4) * 4;
        offset += padded;

        return result;
    }

    /// <summary>
    /// Reads a 32-bit big-endian int and advances the cursor 4 bytes.
    /// </summary>
    private static int DecodeInt(byte[] data, ref int offset)
    {
        // Guard against a truncated packet: never read past the buffer. The blob
        // path has its own length guard; scalars need the same protection.
        if (offset < 0 || offset > data.Length - 4)
        {
            throw new FormatException("OSC int field extends past the received data.");
        }

        byte[] slice = new byte[4];
        Array.Copy(data, offset, slice, 0, 4);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(slice);
        }

        offset += 4;
        return BitConverter.ToInt32(slice, 0);
    }

    /// <summary>
    /// Reads a 32-bit big-endian float and advances the cursor 4 bytes.
    /// </summary>
    private static float DecodeFloat(byte[] data, ref int offset)
    {
        // Guard against a truncated packet: never read past the buffer. The blob
        // path has its own length guard; scalars need the same protection.
        if (offset < 0 || offset > data.Length - 4)
        {
            throw new FormatException("OSC float field extends past the received data.");
        }

        byte[] slice = new byte[4];
        Array.Copy(data, offset, slice, 0, 4);

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(slice);
        }

        offset += 4;
        return BitConverter.ToSingle(slice, 0);
    }

    /// <summary>
    /// Reads a blob: 4-byte big-endian length followed by the content bytes.
    /// Advances the cursor past the trailing padding too.
    /// </summary>
    private static byte[] DecodeBlob(byte[] data, ref int offset)
    {
        int length = DecodeInt(data, ref offset);

        // Guard against a corrupt length: never allocate based on an unvalidated
        // integer from the packet. Compared as "length > data.Length - offset"
        // (not "offset + length > data.Length") so a huge length cannot overflow
        // the addition and slip past into a multi-gigabyte allocation.
        if (length < 0 || length > data.Length - offset)
        {
            throw new FormatException("OSC blob length exceeds the received data.");
        }

        byte[] blob = new byte[length];
        Array.Copy(data, offset, blob, 0, length);

        int paddedLength = ((length + 3) / 4) * 4;
        offset += paddedLength;

        return blob;
    }
}

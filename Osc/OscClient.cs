using System.Net;
using System.Net.Sockets;

namespace M32LiveConsoleTool.Osc;

/// <summary>
/// UDP client that talks to the M32 over the OSC protocol.
/// </summary>
/// <remarks>
/// <para>
/// The M32 uses two default UDP ports:
/// </para>
/// <list type="bullet">
///   <item>Port <c>10023</c>: the client sends commands to the console (faders, preamps, subscriptions).</item>
///   <item>Port <c>10024</c>: the console sends data to the client (meters, parameter-change notifications).</item>
/// </list>
/// <para>
/// To keep receiving parameter-change feedback (for example fader moves echoed
/// back), the client must refresh its <c>/xremote</c> registration every
/// 9 seconds: the console drops it after 10 seconds as a safety measure so it
/// does not keep streaming to disconnected clients. This runs on a separate
/// thread via <see cref="StartKeepAlive"/>. Meter subscriptions are refreshed
/// separately by the <see cref="Modules.MeterReader"/> with <c>/renew</c>.
/// </para>
/// <para>
/// Receiving runs on a second independent thread that listens to the socket
/// continuously and raises <see cref="MessageReceived"/> for every message that
/// arrives. Interested modules (typically <see cref="Modules.MeterReader"/>)
/// subscribe to the event and handle the messages that concern them by OSC
/// address.
/// </para>
/// <para>
/// This class never writes to <see cref="Console"/>: any error (dead socket,
/// malformed message, etc.) is swallowed silently so it does not interfere with
/// the UI dashboard, which owns the screen while running. Critical errors the UI
/// cares about (total loss of meters, sustained connection failure) are detected
/// at the module level.
/// </para>
/// </remarks>
public class OscClient
{
    // ─────────────────────────────────────────────────────────────────────
    // Private fields
    // ─────────────────────────────────────────────────────────────────────

    private UdpClient? udpClient;
    private string mixerIp;
    private int sendPort;
    private int receivePort;

    private Thread? keepAliveThread;
    private Thread? receiveThread;

    private volatile bool isRunning;
    private volatile bool keepAliveRunning;

    /// <summary>Message of the last failed <see cref="Connect"/>, or <c>null</c> if it succeeded.</summary>
    private string? connectError;

    // ─────────────────────────────────────────────────────────────────────
    // Public events
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised for every OSC message arriving from the console. Interested modules
    /// subscribe with <c>oscClient.MessageReceived += myMethod;</c> and receive
    /// each decoded message automatically.
    /// </summary>
    public event Action<OscMessage>? MessageReceived;

    // ─────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an OSC client ready to connect to an M32 at the given IP.
    /// </summary>
    /// <remarks>
    /// The client starts "disconnected": the UDP socket is not opened until
    /// <see cref="Connect"/> is called.
    /// </remarks>
    /// <param name="mixerIp">M32 IP address (e.g. "192.168.1.100").</param>
    /// <param name="sendPort">UDP port the console listens on. Default 10023.</param>
    /// <param name="receivePort">UDP port we listen on for the console's replies. Default 10024.</param>
    public OscClient(string mixerIp, int sendPort = 10023, int receivePort = 10024)
    {
        this.mixerIp = mixerIp;
        this.sendPort = sendPort;
        this.receivePort = receivePort;
        this.udpClient = null;
        this.keepAliveThread = null;
        this.receiveThread = null;
        this.isRunning = false;
        this.keepAliveRunning = false;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public properties
    // ─────────────────────────────────────────────────────────────────────

    public string MixerIp
    {
        get { return mixerIp; }
        set { mixerIp = value; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public methods
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Message of the last failed <see cref="Connect"/>, or <c>null</c> if the last
    /// attempt succeeded. Lets the caller report why no meters will arrive.
    /// </summary>
    public string? ConnectError
    {
        get { return connectError; }
    }

    /// <summary>
    /// Opens the UDP socket on the receive port and starts the thread that listens
    /// for incoming messages.
    /// </summary>
    /// <returns><c>true</c> if the socket opened; <c>false</c> if it could not (the
    /// reason is available in <see cref="ConnectError"/>).</returns>
    public bool Connect()
    {
        try
        {
            udpClient = new UdpClient(receivePort);
            isRunning = true;
            connectError = null;

            receiveThread = new Thread(ReceiveLoop);
            receiveThread.IsBackground = true;
            receiveThread.Name = "OscClient-Receiver";
            receiveThread.Start();
            return true;
        }
        catch (SocketException ex)
        {
            // Could not open the socket. The client stays disconnected; modules that
            // try to send will just see a silent absence of traffic. The caller can
            // read ConnectError to report the cause.
            udpClient = null;
            isRunning = false;
            connectError = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Re-opens the socket if it is currently closed — for example the receive port
    /// was busy when the app started and has since been freed. No-op if already
    /// connected. Lets the meter reader recover a connection that never opened, not
    /// just re-send lost subscriptions.
    /// </summary>
    public void EnsureConnected()
    {
        if (udpClient != null)
        {
            return;
        }
        Connect();
    }

    /// <summary>
    /// Sends an OSC message to the console. If the client is not connected or the
    /// send fails, it is dropped silently.
    /// </summary>
    /// <param name="message">Message to send.</param>
    public void Send(OscMessage message)
    {
        // Read the field once into a local. A concurrent Disconnect() that nulls the
        // field can no longer turn the call below into a NullReferenceException
        // (which is not caught here and, on the keep-alive thread, would crash the
        // whole process during shutdown). If the socket was already closed, the send
        // throws the caught ObjectDisposedException instead.
        UdpClient? client = udpClient;
        if (client == null)
        {
            return;
        }

        try
        {
            byte[] bytes = message.Encode();
            client.Send(bytes, bytes.Length, mixerIp, sendPort);
        }
        catch (SocketException)
        {
            // Transient network error. Ignore.
        }
        catch (ObjectDisposedException)
        {
            // The socket was closed mid-send (program shutting down).
        }
    }

    /// <summary>
    /// Starts a background thread that periodically sends <c>/xremote</c> to the
    /// console so it keeps forwarding parameter changes (such as fader moves).
    /// </summary>
    public void StartKeepAlive()
    {
        if (keepAliveRunning)
        {
            return;
        }

        keepAliveRunning = true;
        keepAliveThread = new Thread(KeepAliveLoop);
        keepAliveThread.IsBackground = true;
        keepAliveThread.Name = "OscClient-KeepAlive";
        keepAliveThread.Start();
    }

    /// <summary>
    /// Cleanly closes the UDP socket and stops the receive and keep-alive threads.
    /// </summary>
    public void Disconnect()
    {
        isRunning = false;
        keepAliveRunning = false;

        if (udpClient != null)
        {
            try
            {
                udpClient.Close();
            }
            catch
            {
                // If the socket was already closed, that's fine.
            }
            udpClient = null;
        }

        if (receiveThread != null && receiveThread.IsAlive)
        {
            receiveThread.Join(1000);
            receiveThread = null;
        }

        if (keepAliveThread != null && keepAliveThread.IsAlive)
        {
            keepAliveThread.Join(1000);
            keepAliveThread = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Private methods: thread loops
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Receive-thread loop. Blocks waiting for messages, decodes them and raises
    /// <see cref="MessageReceived"/>. Any exception (malformed message, dead
    /// socket) is swallowed silently.
    /// </summary>
    private void ReceiveLoop()
    {
        IPEndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);

        while (isRunning)
        {
            try
            {
                byte[] data = udpClient!.Receive(ref remoteEndpoint);
                OscMessage message = OscMessage.Decode(data);
                MessageReceived?.Invoke(message);
            }
            catch (ObjectDisposedException)
            {
                // Socket closed by Disconnect(). Leave the loop cleanly.
                break;
            }
            catch (SocketException)
            {
                if (!isRunning) break;
            }
            catch (Exception)
            {
                // Malformed OSC message or other unexpected exception. Ignore it
                // so one corrupt packet does not kill the thread or garble the
                // dashboard.
            }
        }
    }

    /// <summary>
    /// Keep-alive loop. Every 9 seconds sends an <c>/xremote</c> message so the
    /// console keeps forwarding parameter changes to this client.
    /// </summary>
    private void KeepAliveLoop()
    {
        OscMessage xremote = new OscMessage("/xremote", ",", new List<object>());

        while (keepAliveRunning)
        {
            Send(xremote);

            for (int i = 0; i < 90 && keepAliveRunning; i++)
            {
                Thread.Sleep(100);
            }
        }
    }
}

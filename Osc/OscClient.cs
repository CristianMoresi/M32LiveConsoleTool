using System.Net;
using System.Net.Sockets;

namespace M32LiveConsoleTool.Osc;

/// <summary>IPv4 OSC transport. State changes and control writes share one gate.</summary>
public class OscClient
{
    private UdpClient? udpClient;
    private IPEndPoint destination;
    private readonly int receivePort;
    private Thread? receiveThread;
    private Thread? keepAliveThread;
    private readonly ManualResetEventSlim stop = new(false);
    private volatile bool isRunning;
    private volatile bool keepAliveRunning;
    private long received, rejected, sent, failed, controlSent;

    public object ControlLock { get; } = new();
    public TimeProvider Clock { get; }
    public long Generation { get; private set; }
    public bool WritesInhibited { get; private set; }
    public string? ConnectError { get; private set; }
    public int LocalPort => (udpClient?.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0;
    public long ReceivedCount => Interlocked.Read(ref received);
    public long RejectedCount => Interlocked.Read(ref rejected);
    public long SentCount => Interlocked.Read(ref sent);
    public long SendFailureCount => Interlocked.Read(ref failed);
    public long ControlSentCount => Interlocked.Read(ref controlSent);
    public event Action<OscMessage>? MessageReceived;
    public event Action? SessionInvalidated;

    public OscClient(string mixerIp, int sendPort = 10023, int receivePort = 10024, TimeProvider? clock = null)
    {
        destination = MakeEndpoint(mixerIp, sendPort);
        if (receivePort < 0 || receivePort > 65535) throw new ArgumentOutOfRangeException(nameof(receivePort));
        this.receivePort = receivePort;
        Clock = clock ?? TimeProvider.System;
    }

    private static IPEndPoint MakeEndpoint(string ip, int port)
    {
        if (!IPAddress.TryParse(ip?.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("The mixer requires an IPv4 address.", nameof(ip));
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return new IPEndPoint(address, port);
    }

    public string MixerIp
    {
        get { lock (ControlLock) return destination.Address.ToString(); }
        set
        {
            lock (ControlLock)
            {
                var next = MakeEndpoint(value, destination.Port);
                if (next.Equals(destination)) return;
                destination = next;
                InvalidateSession();
            }
        }
    }

    public void InvalidateSession()
    {
        lock (ControlLock)
        {
            Generation++;
            SessionInvalidated?.Invoke();
        }
    }

    public void InhibitWrites() { lock (ControlLock) WritesInhibited = true; }
    public void ResumeWrites() { lock (ControlLock) WritesInhibited = false; }

    public bool Connect()
    {
        lock (ControlLock)
        {
            if (isRunning) return true;
            try
            {
                var socket = new UdpClient(new IPEndPoint(IPAddress.Any, receivePort));
                udpClient = socket;
                stop.Reset();
                isRunning = true;
                ConnectError = null;
                InvalidateSession();
                receiveThread = new Thread(() => ReceiveLoop(socket)) { IsBackground = true, Name = "OSC receiver" };
                receiveThread.Start();
                return true;
            }
            catch (SocketException ex)
            {
                ConnectError = ex.Message;
                return false;
            }
        }
    }

    public void EnsureConnected() { if (!isRunning) Connect(); }

    public bool Send(OscMessage message)
    {
        lock (ControlLock)
        {
            if (message.Arguments.Any(x => x is float f && !float.IsFinite(f))) return false;
            try
            {
                if (SendCore(message, destination)) { Interlocked.Increment(ref sent); return true; }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or ArgumentException)
            {
                ConnectError = ex.Message;
            }
            Interlocked.Increment(ref failed);
            return false;
        }
    }

    // Injectable at the transport boundary: tests exercise the same write gate.
    protected virtual bool SendCore(OscMessage message, IPEndPoint endpoint)
    {
        if (udpClient == null) return false;
        byte[] bytes = message.Encode();
        return udpClient.Send(bytes, bytes.Length, endpoint) == bytes.Length;
    }

    public bool SendControl(OscMessage message)
    {
        lock (ControlLock)
        {
            if (WritesInhibited || !Send(message)) return false;
            Interlocked.Increment(ref controlSent);
            return true;
        }
    }

    public void StartKeepAlive()
    {
        if (keepAliveRunning) return;
        keepAliveRunning = true;
        keepAliveThread = new Thread(() =>
        {
            var request = new OscMessage("/xremote", ",", new());
            while (keepAliveRunning)
            {
                Send(request);
                if (stop.Wait(9000)) break;
            }
        })
        { IsBackground = true, Name = "OSC keepalive" };
        keepAliveThread.Start();
    }

    public void Disconnect()
    {
        lock (ControlLock)
        {
            isRunning = false;
            keepAliveRunning = false;
            stop.Set();
            udpClient?.Dispose();
            udpClient = null;
            InvalidateSession();
        }
        if (receiveThread != Thread.CurrentThread) receiveThread?.Join(1000);
        if (keepAliveThread != Thread.CurrentThread) keepAliveThread?.Join(1000);
    }

    private void ReceiveLoop(UdpClient socket)
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (isRunning)
        {
            try
            {
                byte[] data = socket.Receive(ref remote);
                lock (ControlLock)
                {
                    if (!isRunning || !remote.Equals(destination)) { Interlocked.Increment(ref rejected); continue; }
                    var message = OscMessage.Decode(data);
                    Interlocked.Increment(ref received);
                    MessageReceived?.Invoke(message);
                }
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (!isRunning) break; Interlocked.Increment(ref rejected); }
            catch (Exception) { Interlocked.Increment(ref rejected); }
        }
    }
}

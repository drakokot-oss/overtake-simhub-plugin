using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Overtake.SimHub.Plugin
{
    /// <summary>
    /// Listens for F1 25 UDP telemetry on a dedicated port and optionally
    /// forwards each packet to another local port (e.g., SimHub's native reader on 20777).
    /// This avoids the Windows limitation where two sockets on the same port
    /// only deliver datagrams to one listener.
    /// </summary>
    public class UdpReceiver : IDisposable
    {
        private UdpClient _client;
        private UdpClient _forwarder;
        private Thread _listenThread;
        private volatile bool _running;
        private readonly ConcurrentQueue<byte[]> _packetQueue;

        private long _packetsReceived;
        private string _status;
        private string _lastError;
        private ulong _lastSessionUid;
        private byte _lastPacketId;
        private int _listenPort;
        private int _forwardPort;

        public UdpReceiver()
        {
            _packetQueue = new ConcurrentQueue<byte[]>();
            _packetsReceived = 0;
            _status = "Stopped";
            _lastError = "";
        }

        public ConcurrentQueue<byte[]> PacketQueue
        {
            get { return _packetQueue; }
        }

        public long PacketsReceived
        {
            get { return _packetsReceived; }
        }

        public string Status
        {
            get { return _status; }
        }

        public string LastError
        {
            get { return _lastError; }
        }

        public ulong LastSessionUid
        {
            get { return _lastSessionUid; }
        }

        public byte LastPacketId
        {
            get { return _lastPacketId; }
        }

        /// <param name="port">Port to listen on (F1 25 should send here).</param>
        /// <param name="forwardPort">
        /// Port to forward packets to (SimHub's native reader). Set to 0 to disable forwarding.
        /// Forwarding is automatically skipped when forwardPort == port.
        /// </param>
        public void Start(int port, int forwardPort = 20777)
        {
            if (_running) return;
            _listenPort = port;
            _forwardPort = forwardPort;

            try
            {
                _client = new UdpClient();
                _client.ExclusiveAddressUse = false;
                _client.Client.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.ReuseAddress,
                    true);
                _client.Client.Bind(new IPEndPoint(IPAddress.Any, port));

                bool shouldForward = forwardPort > 0 && forwardPort != port;
                if (shouldForward)
                {
                    _forwarder = new UdpClient();
                    _forwarder.Connect(IPAddress.Loopback, forwardPort);
                }

                _running = true;
                _status = "Listening";
                _lastError = "";

                _listenThread = new Thread(ListenLoop);
                _listenThread.IsBackground = true;
                _listenThread.Name = "OvertakeUdpListener";
                _listenThread.Start();

                string fwdMsg = shouldForward
                    ? string.Format(", forwarding to localhost:{0}", forwardPort)
                    : "";
                global::SimHub.Logging.Current.Info(
                    string.Format("[Overtake] UDP listener started on port {0}{1}", port, fwdMsg));
            }
            catch (Exception ex)
            {
                _status = "Error";
                _lastError = ex.Message;
                global::SimHub.Logging.Current.Error(
                    string.Format("[Overtake] Failed to start UDP listener: {0}", ex.Message));
            }
        }

        public void Stop()
        {
            _running = false;

            try
            {
                if (_client != null)
                    _client.Close();
            }
            catch { }

            try
            {
                if (_forwarder != null)
                    _forwarder.Close();
            }
            catch { }

            if (_listenThread != null && _listenThread.IsAlive)
            {
                _listenThread.Join(2000);
            }

            _client = null;
            _forwarder = null;
            _listenThread = null;
            _status = "Stopped";
            global::SimHub.Logging.Current.Info("[Overtake] UDP listener stopped");
        }

        private void ListenLoop()
        {
            var remoteEp = new IPEndPoint(IPAddress.Any, 0);

            while (_running)
            {
                try
                {
                    byte[] data = _client.Receive(ref remoteEp);
                    if (data == null || data.Length < 29) continue;

                    ParseHeader(data);
                    _packetQueue.Enqueue(data);
                    _packetsReceived++;

                    if (_forwarder != null)
                    {
                        try { _forwarder.Send(data, data.Length); }
                        catch { }
                    }
                }
                catch (SocketException)
                {
                    if (!_running) break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                    {
                        global::SimHub.Logging.Current.Warn(
                            string.Format("[Overtake] UDP receive error: {0}", ex.Message));
                        Thread.Sleep(100);
                    }
                }
            }
        }

        private void ParseHeader(byte[] data)
        {
            _lastPacketId = data[6];
            _lastSessionUid = BitConverter.ToUInt64(data, 7);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}

// ============================================================================
//  Client UDP del canale di controllo.
//
//  Socket non bloccante, drenato una volta per frame da Update(). Su localhost
//  la perdita di pacchetti e' praticamente nulla e il round trip e' sotto le
//  decine di microsecondi, quindi un thread dedicato non aggiungerebbe niente
//  e aggiungerebbe invece sincronizzazione da sbagliare.
//
//  IL PROTOCOLLO E' PENSATO PER TOLLERARE LA PERDITA:
//   - un POSE perso e' irrilevante: e' latest-wins, il prossimo lo rimpiazza;
//   - uno STATUS perso e' irrilevante: quello successivo porta una sequenza piu'
//     alta e il frame corrispondente;
//   - solo l'HANDSHAKE conta davvero, e per quello l'HELLO viene ripetuto
//     finche' non arriva la risposta alla richiesta corrente.
//
//  Ogni pacchetto ricevuto viene consegnato con un evento, UNO PER UNO: chi
//  consuma i frame ha bisogno di vedere TUTTI gli STATUS arrivati nello stesso
//  frame (per sapere quali buffer sono rimasti indietro), non solo l'ultimo.
// ============================================================================

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace GpuShareSpike
{
    public sealed class ControlChannelClient : IDisposable
    {
        private Socket _socket;
        private readonly byte[] _receiveBuffer = new byte[2048];
        private readonly byte[] _sendBuffer = new byte[512];
        private uint _unityPid;

        /// <summary>Ogni HANDSHAKE valido ricevuto.</summary>
        public event Action<Protocol.HandshakeInfo> HandshakeReceived;

        /// <summary>Ogni STATUS valido ricevuto, nell'ordine di arrivo.</summary>
        public event Action<Protocol.StatusInfo> StatusReceived;

        public bool IsOpen => _socket != null;

        public Protocol.StatusInfo LastStatus { get; private set; }
        public bool HasStatus { get; private set; }

        public ulong PosePacketsSent { get; private set; }
        public ulong StatusPacketsReceived { get; private set; }
        public ulong HandshakesReceived { get; private set; }
        public ulong BadPacketsReceived { get; private set; }
        public string LastError { get; private set; } = string.Empty;

        /// <summary>Non vuoto se Unreal parla un'altra versione del protocollo.</summary>
        public string VersionMismatch { get; private set; } = string.Empty;

        /// <summary>Timestamp QPC. Su Windows Stopwatch.GetTimestamp() E' QueryPerformanceCounter.</summary>
        public static long Qpc() => Stopwatch.GetTimestamp();
        public static double QpcToMilliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        public bool Open(string host, int port, out string error)
        {
            error = null;
            Close();

            try
            {
                _unityPid = (uint)Process.GetCurrentProcess().Id;

                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    Blocking = false,
                    // Buffer generosi: qui non ci interessa il throughput, ma
                    // non vogliamo che un frame lungo faccia scartare pacchetti.
                    ReceiveBufferSize = 1 << 20,
                    SendBufferSize = 1 << 20,
                };

                var remote = new IPEndPoint(IPAddress.Parse(host), port);
                // Connect su UDP non apre nulla: fissa solo il peer, cosi'
                // possiamo usare Send/Receive invece di SendTo/ReceiveFrom.
                _socket.Connect(remote);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                LastError = error;
                Close();
                return false;
            }
        }

        public void Close()
        {
            if (_socket != null)
            {
                try { _socket.Close(); } catch { /* chiusura best effort */ }
                _socket = null;
            }
            HasStatus = false;
        }

        public void Dispose() => Close();

        public void SendHello(uint adapterLuidLow, int adapterLuidHigh, Protocol.HelloFlags flags,
                              int viewCount, int viewWidth, int viewHeight, uint requestId)
        {
            if (_socket == null) return;
            int size = Protocol.WriteHello(_sendBuffer, _unityPid, adapterLuidLow, adapterLuidHigh, flags,
                                           viewCount, viewWidth, viewHeight, requestId);
            TrySend(size);
        }

        public void SendBye()
        {
            if (_socket == null) return;
            int size = Protocol.WriteBye(_sendBuffer, _unityPid);
            TrySend(size);
        }

        public void SendPose(ulong frameId, long qpc, Protocol.ViewPose[] views, int viewCount, float nearM, float farM)
        {
            if (_socket == null) return;

            int size = Protocol.WritePose(_sendBuffer, frameId, qpc, views, viewCount, nearM, farM);
            if (TrySend(size))
            {
                ++PosePacketsSent;
            }
        }

        /// <summary>Drena tutti i pacchetti in arrivo. Da chiamare una volta per frame.</summary>
        public void Poll()
        {
            if (_socket == null) return;

            while (true)
            {
                int received;
                try
                {
                    if (_socket.Available <= 0) break;
                    received = _socket.Receive(_receiveBuffer);
                }
                catch (SocketException exception)
                {
                    if (exception.SocketErrorCode == SocketError.WouldBlock) break;
                    // ConnectionReset su UDP significa "ICMP port unreachable":
                    // Unreal non e' ancora partito. Non e' un errore fatale.
                    if (exception.SocketErrorCode == SocketError.ConnectionReset) break;
                    LastError = exception.Message;
                    break;
                }

                if (received <= 0) break;
                HandlePacket(new ReadOnlySpan<byte>(_receiveBuffer, 0, received));
            }
        }

        private void HandlePacket(ReadOnlySpan<byte> data)
        {
            Protocol.HeaderResult header = Protocol.ReadHeader(data, out Protocol.PacketType type, out ushort version);

            if (header == Protocol.HeaderResult.VersionMismatch)
            {
                ++BadPacketsReceived;
                // Il sintomo altrimenti sarebbe solo "in attesa dell'handshake"
                // per sempre, senza spiegazione.
                VersionMismatch = $"Unreal parla il protocollo v{version}, Unity v{Protocol.Version}: " +
                                  "ricompila il lato rimasto indietro (modulo Unreal, oppure plugin nativo + script).";
                return;
            }
            if (header != Protocol.HeaderResult.Ok)
            {
                ++BadPacketsReceived;
                return;
            }

            switch (type)
            {
                case Protocol.PacketType.Handshake:
                    if (Protocol.TryReadHandshake(data, out Protocol.HandshakeInfo handshake))
                    {
                        ++HandshakesReceived;
                        HandshakeReceived?.Invoke(handshake);
                    }
                    else
                    {
                        ++BadPacketsReceived;
                    }
                    break;

                case Protocol.PacketType.Status:
                    if (Protocol.TryReadStatus(data, out Protocol.StatusInfo status))
                    {
                        LastStatus = status;
                        HasStatus = true;
                        ++StatusPacketsReceived;
                        StatusReceived?.Invoke(status);
                    }
                    else
                    {
                        ++BadPacketsReceived;
                    }
                    break;

                default:
                    ++BadPacketsReceived;
                    break;
            }
        }

        private bool TrySend(int size)
        {
            try
            {
                _socket.Send(_sendBuffer, 0, size, SocketFlags.None);
                return true;
            }
            catch (SocketException exception)
            {
                if (exception.SocketErrorCode != SocketError.WouldBlock &&
                    exception.SocketErrorCode != SocketError.ConnectionReset)
                {
                    LastError = exception.Message;
                }
                return false;
            }
        }
    }
}

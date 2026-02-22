using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ErenshorDedicatedServer.Configuration;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ErenshorDedicatedServer.Network
{
    /// <summary>
    /// Manages all network connections using LiteNetLib. Handles connection/disconnection,
    /// packet routing, rate limiting, and broadcast operations.
    /// </summary>
    public class NetworkManager : INetEventListener, IDisposable
    {
        private NetManager _netManager;
        private readonly ServerConfig _config;
        private readonly ConcurrentDictionary<int, PlayerSession> _peerToSession = new();
        private readonly ConcurrentDictionary<short, PlayerSession> _idToSession = new();
        private readonly object _idLock = new object();
        private short _nextPlayerId = 1;

        // Packet processing
        public event Action<PlayerSession, PacketType, NetDataReader> OnPacketReceived;
        public event Action<PlayerSession> OnPlayerConnected;
        public event Action<PlayerSession, DisconnectInfo> OnPlayerDisconnected;

        // Stats
        public long TotalBytesReceived { get; private set; }
        public long TotalBytesSent { get; private set; }
        public long TotalPacketsReceived { get; private set; }
        public long TotalPacketsSent { get; private set; }
        public int ConnectedPlayerCount => _idToSession.Count;

        public IReadOnlyDictionary<short, PlayerSession> Players => _idToSession;

        public NetworkManager(ServerConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public bool Start()
        {
            try
            {
                _netManager = new NetManager(this)
                {
                    AutoRecycle = true,
                    UpdateTime = Math.Max(1, 1000 / _config.TickRate),
                    DisconnectTimeout = _config.ConnectionTimeoutSeconds * 1000,
                    ChannelsCount = (byte)_config.MaxChannels,
                    EnableStatistics = true,
                    UnconnectedMessagesEnabled = false,
                    NatPunchEnabled = false,
                };

                if (!_netManager.Start(_config.Port))
                {
                    ServerLogger.Error($"Failed to bind to port {_config.Port}");
                    return false;
                }

                ServerLogger.Info($"Network listening on port {_config.Port}", "NET");
                return true;
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Network start failed: {ex.Message}", "NET");
                return false;
            }
        }

        public void Stop()
        {
            try
            {
                foreach (var session in _idToSession.Values.ToArray())
                {
                    try
                    {
                        session.Peer?.Disconnect();
                    }
                    catch { /* best effort */ }
                }

                _netManager?.Stop(true);
                _peerToSession.Clear();
                _idToSession.Clear();
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Network stop error: {ex.Message}", "NET");
            }
        }

        public void PollEvents()
        {
            _netManager?.PollEvents();
        }

        public void Dispose()
        {
            Stop();
            _netManager = null;
        }

        // ----- INetEventListener -----

        public void OnPeerConnected(NetPeer peer)
        {
            if (peer == null) return;

            if (_idToSession.Count >= _config.MaxPlayers)
            {
                ServerLogger.Warning($"Rejecting connection from {peer.Address}: server full", "NET");
                peer.Disconnect();
                return;
            }

            var session = new PlayerSession(peer);
            short playerId;

            lock (_idLock)
            {
                playerId = AllocatePlayerId();
            }

            if (playerId < 0)
            {
                ServerLogger.Warning($"Rejecting connection: no available player IDs", "NET");
                peer.Disconnect();
                return;
            }

            session.PlayerId = playerId;
            _peerToSession[peer.Id] = session;
            _idToSession[playerId] = session;

            ServerLogger.Info($"Player connected: Peer {peer.Id} -> ID {playerId} from {peer.Address}", "NET");

            // Send the player their assigned ID
            SendServerConnect(session);

            // Send server settings
            SendServerSettings(session);

            // Send mod list (empty for dedicated server)
            SendHostMods(session);

            OnPlayerConnected?.Invoke(session);
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (peer == null) return;

            if (_peerToSession.TryRemove(peer.Id, out var session))
            {
                _idToSession.TryRemove(session.PlayerId, out _);
                ServerLogger.Info($"Player disconnected: [{session.PlayerId}] {session.CharacterName} ({disconnectInfo.Reason})", "NET");
                OnPlayerDisconnected?.Invoke(session, disconnectInfo);
            }
        }

        public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError)
        {
            ServerLogger.Warning($"Network error from {endPoint}: {socketError}", "NET");
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (peer == null || reader == null) return;

            TotalPacketsReceived++;
            TotalBytesReceived += reader.AvailableBytes;

            if (!_peerToSession.TryGetValue(peer.Id, out var session))
            {
                ServerLogger.Debug($"Packet from unknown peer {peer.Id}", "NET");
                return;
            }

            // Rate limiting
            if (!session.CheckPacketRate(_config.MaxPacketsPerSecond))
            {
                ServerLogger.Warning($"Rate limited: [{session.PlayerId}] {session.CharacterName}", "NET");
                return;
            }

            try
            {
                if (reader.AvailableBytes < 1)
                {
                    ServerLogger.Debug($"Empty packet from [{session.PlayerId}]", "NET");
                    return;
                }

                var packetType = (PacketType)reader.GetByte();
                OnPacketReceived?.Invoke(session, packetType, reader);
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Packet processing error from [{session.PlayerId}]: {ex.Message}", "NET");
                ServerLogger.Debug($"Stack trace: {ex.StackTrace}", "NET");
            }
        }

        public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            // Not used - unconnected messages disabled
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            if (peer != null && _peerToSession.TryGetValue(peer.Id, out var session))
            {
                // Latency data available for player list
            }
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            if (request == null) return;

            if (_idToSession.Count >= _config.MaxPlayers)
            {
                request.Reject();
                ServerLogger.Info($"Connection rejected from {request.RemoteEndPoint}: server full", "NET");
                return;
            }

            // Accept all connections for now; auth happens post-connect via packet exchange
            request.Accept();
        }

        // ----- Public Send Methods -----

        /// <summary>
        /// Send raw data to a specific player.
        /// </summary>
        public void SendTo(PlayerSession session, NetDataWriter writer, DeliveryMethod delivery, byte channel)
        {
            if (session?.Peer == null || writer == null) return;

            try
            {
                session.Peer.Send(writer, channel, delivery);
                TotalPacketsSent++;
                TotalBytesSent += writer.Length;
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Send error to [{session.PlayerId}]: {ex.Message}", "NET");
            }
        }

        /// <summary>
        /// Send raw data to a specific player by ID.
        /// </summary>
        public void SendTo(short playerId, NetDataWriter writer, DeliveryMethod delivery, byte channel)
        {
            if (_idToSession.TryGetValue(playerId, out var session))
                SendTo(session, writer, delivery, channel);
        }

        /// <summary>
        /// Broadcast data to all connected players.
        /// </summary>
        public void Broadcast(NetDataWriter writer, DeliveryMethod delivery, byte channel, short excludeId = -1)
        {
            if (writer == null) return;

            foreach (var kvp in _idToSession)
            {
                if (kvp.Key == excludeId) continue;
                SendTo(kvp.Value, writer, delivery, channel);
            }
        }

        /// <summary>
        /// Broadcast data to all players in a specific zone.
        /// </summary>
        public void BroadcastToZone(string zone, NetDataWriter writer, DeliveryMethod delivery, byte channel, short excludeId = -1)
        {
            if (string.IsNullOrEmpty(zone) || writer == null) return;

            foreach (var kvp in _idToSession)
            {
                if (kvp.Key == excludeId) continue;
                if (kvp.Value.Zone == zone)
                    SendTo(kvp.Value, writer, delivery, channel);
            }
        }

        /// <summary>
        /// Broadcast data to a specific list of player IDs.
        /// </summary>
        public void BroadcastToPlayers(IEnumerable<short> playerIds, NetDataWriter writer, DeliveryMethod delivery, byte channel)
        {
            if (playerIds == null || writer == null) return;

            foreach (var id in playerIds)
            {
                SendTo(id, writer, delivery, channel);
            }
        }

        /// <summary>
        /// Disconnect a player by session.
        /// </summary>
        public void DisconnectPlayer(PlayerSession session, string reason = "")
        {
            if (session?.Peer == null) return;

            ServerLogger.Info($"Disconnecting [{session.PlayerId}] {session.CharacterName}: {reason}", "NET");

            // Send disconnect packet to others
            BroadcastPlayerDisconnect(session.PlayerId);

            session.Peer.Disconnect();
        }

        /// <summary>
        /// Get a player session by ID.
        /// </summary>
        public PlayerSession GetSession(short playerId)
        {
            _idToSession.TryGetValue(playerId, out var session);
            return session;
        }

        /// <summary>
        /// Get a player session by peer.
        /// </summary>
        public PlayerSession GetSession(NetPeer peer)
        {
            if (peer == null) return null;
            _peerToSession.TryGetValue(peer.Id, out var session);
            return session;
        }

        /// <summary>
        /// Get all sessions as a list.
        /// </summary>
        public List<PlayerSession> GetAllSessions()
        {
            return _idToSession.Values.ToList();
        }

        /// <summary>
        /// Get all sessions in a specific zone.
        /// </summary>
        public List<PlayerSession> GetSessionsInZone(string zone)
        {
            if (string.IsNullOrEmpty(zone)) return new List<PlayerSession>();
            return _idToSession.Values.Where(s => s.Zone == zone).ToList();
        }

        // ----- Private Helpers -----

        private short AllocatePlayerId()
        {
            // Find next available ID
            for (int attempts = 0; attempts < short.MaxValue; attempts++)
            {
                if (_nextPlayerId <= 0 || _nextPlayerId >= short.MaxValue)
                    _nextPlayerId = 1;

                var id = _nextPlayerId++;
                if (!_idToSession.ContainsKey(id))
                    return id;
            }
            return -1;
        }

        private void SendServerConnect(PlayerSession session)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.SERVER_CONNECT);
            writer.Put(session.PlayerId);
            SendTo(session, writer, DeliveryMethod.ReliableOrdered, PacketHelper.GetChannel(PacketType.SERVER_CONNECT));
        }

        private void SendServerSettings(PlayerSession session)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.SERVER_INFO);

            var flags = new HashSet<ServerInfoType> { ServerInfoType.SERVER_SETTINGS, ServerInfoType.PVP_MODE };
            writer.Put(PacketHelper.GetSubTypeFlag(flags));

            // PVP mode
            writer.Put(_config.PvpEnabled);

            // Settings
            writer.Put(_config.HpModifier);
            writer.Put(_config.XpModifier);
            writer.Put(_config.DamageModifier);
            writer.Put(_config.LootRateModifier);

            SendTo(session, writer, DeliveryMethod.ReliableOrdered, PacketHelper.GetChannel(PacketType.SERVER_INFO));
        }

        private void SendHostMods(PlayerSession session)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.SERVER_INFO);

            var flags = new HashSet<ServerInfoType> { ServerInfoType.HOST_MODS };
            writer.Put(PacketHelper.GetSubTypeFlag(flags));

            // Send our version info as a single mod entry
            writer.Put(1); // count
            writer.Put("Erenshor Coop");
            writer.Put("1.0.0");

            SendTo(session, writer, DeliveryMethod.ReliableOrdered, PacketHelper.GetChannel(PacketType.SERVER_INFO));
        }

        public void BroadcastPlayerDisconnect(short playerId)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.DISCONNECT);
            writer.Put(playerId);
            Broadcast(writer, DeliveryMethod.ReliableOrdered, PacketHelper.GetChannel(PacketType.DISCONNECT), playerId);
        }

        public void SendPlayerList()
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.SERVER_INFO);

            var flags = new HashSet<ServerInfoType> { ServerInfoType.PLAYER_LIST };
            writer.Put(PacketHelper.GetSubTypeFlag(flags));

            var sessions = _idToSession.Values.ToArray();
            writer.Put(sessions.Length);
            foreach (var s in sessions)
            {
                writer.Put(s.PlayerId);
                writer.Put(s.Peer?.Ping ?? 0);
                writer.Put(s.IsModerator);
                writer.Put(false); // isHost - dedicated server has no host player
                writer.Put(s.IsAdmin);
            }

            Broadcast(writer, DeliveryMethod.ReliableOrdered, PacketHelper.GetChannel(PacketType.SERVER_INFO));
        }
    }
}

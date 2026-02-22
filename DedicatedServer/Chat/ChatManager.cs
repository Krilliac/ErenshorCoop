using System;
using System.Collections.Generic;
using System.Linq;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Configuration;
using ErenshorDedicatedServer.Data;
using ErenshorDedicatedServer.Network;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ErenshorDedicatedServer.Chat
{
    /// <summary>
    /// Handles all chat functionality: say, shout, whisper, group, server info messages,
    /// combat log relay, and chat filtering/rate limiting.
    /// </summary>
    public class ChatManager
    {
        private readonly NetworkManager _network;
        private readonly ServerConfig _config;

        // Chat filter (basic profanity/spam filter - expandable)
        private readonly HashSet<string> _mutedPlayers = new();

        public ChatManager(NetworkManager network, ServerConfig config)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public void HandleMessage(PlayerSession session, MessageType type, string message,
            string target, short sender, string color, bool append, bool isCombatLog)
        {
            if (session == null) return;
            if (string.IsNullOrWhiteSpace(message)) return;

            // Sanitize
            message = PacketHelper.Sanitize(message, 512);

            // Rate limit check
            if (!session.CheckChatRate(_config.MaxChatMessagesPerSecond))
            {
                SendInfoMessage(session, "[Server] You are sending messages too fast.");
                return;
            }

            // Mute check
            if (_mutedPlayers.Contains(session.CharacterName.ToLowerInvariant()))
            {
                SendInfoMessage(session, "[Server] You are muted.");
                return;
            }

            // Message length validation
            if (message.Length > 512)
                message = message.Substring(0, 512);

            switch (type)
            {
                case MessageType.SAY:
                    HandleSay(session, message);
                    break;
                case MessageType.SHOUT:
                    HandleShout(session, message);
                    break;
                case MessageType.WHISPER:
                    HandleWhisper(session, message, target);
                    break;
                case MessageType.GROUP:
                    HandleGroupChat(session, message);
                    break;
                case MessageType.INFO:
                    // Client shouldn't send INFO type; ignore
                    break;
                case MessageType.BATTLE_LOG:
                    HandleBattleLog(session, message, sender, color, append, isCombatLog);
                    break;
            }
        }

        // ====================================================
        // SAY - visible to zone players
        // ====================================================
        private void HandleSay(PlayerSession session, string message)
        {
            ServerLogger.Info($"[SAY] [{session.PlayerId}] {session.CharacterName}: {message}", "CHAT");

            var writer = BuildMessagePacket(session.PlayerId, MessageType.SAY, message);
            _network.BroadcastToZone(session.Zone, writer, DeliveryMethod.ReliableOrdered,
                PacketHelper.GetChannel(PacketType.PLAYER_MESSAGE), session.PlayerId);
        }

        // ====================================================
        // SHOUT - visible to all players on server
        // ====================================================
        private void HandleShout(PlayerSession session, string message)
        {
            ServerLogger.Info($"[SHOUT] [{session.PlayerId}] {session.CharacterName}: {message}", "CHAT");

            var writer = BuildMessagePacket(session.PlayerId, MessageType.SHOUT, message);
            _network.Broadcast(writer, DeliveryMethod.ReliableOrdered,
                PacketHelper.GetChannel(PacketType.PLAYER_MESSAGE), session.PlayerId);
        }

        // ====================================================
        // WHISPER - private message to named target
        // ====================================================
        private void HandleWhisper(PlayerSession session, string message, string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
            {
                SendInfoMessage(session, "[Server] Whisper target not specified.");
                return;
            }

            targetName = PacketHelper.Sanitize(targetName, 64);

            var targetSession = _network.GetAllSessions()
                .FirstOrDefault(s => string.Equals(s.CharacterName, targetName, StringComparison.OrdinalIgnoreCase));

            if (targetSession == null)
            {
                SendInfoMessage(session, $"[Server] Player '{targetName}' not found.");
                return;
            }

            if (targetSession.PlayerId == session.PlayerId)
            {
                SendInfoMessage(session, "[Server] You cannot whisper yourself.");
                return;
            }

            ServerLogger.Debug($"[WHISPER] [{session.PlayerId}] {session.CharacterName} -> {targetName}: {message}", "CHAT");

            var writer = BuildWhisperPacket(session.PlayerId, message, targetName);
            _network.SendTo(targetSession, writer, DeliveryMethod.ReliableOrdered,
                PacketHelper.GetChannel(PacketType.PLAYER_MESSAGE));
        }

        // ====================================================
        // GROUP CHAT - visible to group members
        // ====================================================
        private void HandleGroupChat(PlayerSession session, string message)
        {
            if (session.GroupId < 0)
            {
                SendInfoMessage(session, "[Server] You are not in a group.");
                return;
            }

            ServerLogger.Debug($"[GROUP] [{session.PlayerId}] {session.CharacterName}: {message}", "CHAT");

            var writer = BuildMessagePacket(session.PlayerId, MessageType.GROUP, message);

            // Send to all group members
            foreach (var other in _network.GetAllSessions())
            {
                if (other.GroupId == session.GroupId && other.PlayerId != session.PlayerId)
                {
                    _network.SendTo(other, writer, DeliveryMethod.ReliableOrdered,
                        PacketHelper.GetChannel(PacketType.PLAYER_MESSAGE));
                }
            }
        }

        // ====================================================
        // BATTLE LOG - relay combat messages
        // ====================================================
        private void HandleBattleLog(PlayerSession session, string message, short sender,
            string color, bool append, bool isCombatLog)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.PLAYER_MESSAGE);
            writer.Put(session.PlayerId);
            writer.Put((byte)MessageType.BATTLE_LOG);
            writer.Put(message);
            writer.Put(sender);
            writer.Put(color ?? "");
            writer.Put(append);
            writer.Put(isCombatLog);

            _network.BroadcastToZone(session.Zone, writer, DeliveryMethod.ReliableOrdered,
                PacketHelper.GetChannel(PacketType.PLAYER_MESSAGE), session.PlayerId);
        }

        // ====================================================
        // SERVER -> CLIENT MESSAGES
        // ====================================================

        /// <summary>
        /// Send an info message to a specific player from the server.
        /// </summary>
        public void SendInfoMessage(PlayerSession session, string message)
        {
            if (session == null || string.IsNullOrEmpty(message)) return;

            var writer = BuildMessagePacket(0, MessageType.INFO, message);
            _network.SendTo(session, writer, DeliveryMethod.ReliableOrdered,
                PacketHelper.GetChannel(PacketType.PLAYER_MESSAGE));
        }

        /// <summary>
        /// Send an info message to a player by ID.
        /// </summary>
        public void SendInfoMessage(short playerId, string message)
        {
            var session = _network.GetSession(playerId);
            if (session != null)
                SendInfoMessage(session, message);
        }

        /// <summary>
        /// Broadcast an info message to all connected players.
        /// </summary>
        public void BroadcastInfoMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            ServerLogger.Info($"[BROADCAST] {message}", "CHAT");

            var writer = BuildMessagePacket(0, MessageType.INFO, message);
            _network.Broadcast(writer, DeliveryMethod.ReliableOrdered,
                PacketHelper.GetChannel(PacketType.PLAYER_MESSAGE));
        }

        // ====================================================
        // MUTE MANAGEMENT
        // ====================================================

        public bool MutePlayer(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return _mutedPlayers.Add(name.ToLowerInvariant());
        }

        public bool UnmutePlayer(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return _mutedPlayers.Remove(name.ToLowerInvariant());
        }

        public bool IsPlayerMuted(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return _mutedPlayers.Contains(name.ToLowerInvariant());
        }

        public IReadOnlyCollection<string> GetMutedPlayers() => _mutedPlayers;

        // ====================================================
        // PACKET BUILDERS
        // ====================================================

        private static NetDataWriter BuildMessagePacket(short entityId, MessageType type, string message)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.PLAYER_MESSAGE);
            writer.Put(entityId);
            writer.Put((byte)type);
            writer.Put(message ?? "");
            return writer;
        }

        private static NetDataWriter BuildWhisperPacket(short entityId, string message, string target)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.PLAYER_MESSAGE);
            writer.Put(entityId);
            writer.Put((byte)MessageType.WHISPER);
            writer.Put(message ?? "");
            writer.Put(target ?? "");
            return writer;
        }
    }
}

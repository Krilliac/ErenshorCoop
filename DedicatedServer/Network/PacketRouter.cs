using System;
using System.Collections.Generic;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ErenshorDedicatedServer.Network
{
    /// <summary>
    /// Decodes incoming packets from the client protocol and dispatches to the appropriate handler.
    /// This is the main packet-parsing layer between raw network data and game logic.
    /// </summary>
    public class PacketRouter
    {
        private readonly NetworkManager _network;
        private readonly ServerCore _server;

        public PacketRouter(NetworkManager network, ServerCore server)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        public void HandlePacket(PlayerSession session, PacketType type, NetDataReader reader)
        {
            if (session == null || reader == null) return;

            try
            {
                switch (type)
                {
                    case PacketType.PLAYER_CONNECT:
                        HandlePlayerConnect(session, reader);
                        break;
                    case PacketType.PLAYER_DATA:
                        HandlePlayerData(session, reader);
                        break;
                    case PacketType.PLAYER_TRANSFORM:
                        HandlePlayerTransform(session, reader);
                        break;
                    case PacketType.PLAYER_ACTION:
                        HandlePlayerAction(session, reader);
                        break;
                    case PacketType.PLAYER_MESSAGE:
                        HandlePlayerMessage(session, reader);
                        break;
                    case PacketType.PLAYER_REQUEST:
                        HandlePlayerRequest(session, reader);
                        break;
                    case PacketType.ENTITY_DATA:
                        HandleEntityData(session, reader);
                        break;
                    case PacketType.ENTITY_SPAWN:
                        HandleEntitySpawn(session, reader);
                        break;
                    case PacketType.ENTITY_TRANSFORM:
                        HandleEntityTransform(session, reader);
                        break;
                    case PacketType.ENTITY_ACTION:
                        HandleEntityAction(session, reader);
                        break;
                    case PacketType.GROUP:
                        HandleGroupPacket(session, reader);
                        break;
                    case PacketType.ITEM_DROP:
                        HandleItemDrop(session, reader);
                        break;
                    case PacketType.WEATHER_DATA:
                        HandleWeatherData(session, reader);
                        break;
                    default:
                        ServerLogger.Debug($"Unknown packet type {type} from [{session.PlayerId}]", "PKT");
                        break;
                }
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Error handling {type} from [{session.PlayerId}]: {ex.Message}", "PKT");
                ServerLogger.Debug($"Stack trace: {ex.StackTrace}", "PKT");
            }
        }

        // ====================================================
        // PLAYER CONNECT
        // ====================================================
        private void HandlePlayerConnect(PlayerSession session, NetDataReader reader)
        {
            var entityId = reader.GetShort();
            var isSim = reader.GetBool();

            if (isSim)
            {
                // Sim connection packet: relay to zone players
                RelayToZone(session, PacketType.PLAYER_CONNECT, reader, rewindFull: true);
                return;
            }

            // Player connection data
            var flag = reader.GetUShort();
            var dataTypes = PacketHelper.ReadSubTypeFlag<PlayerDataType>(flag);

            if (dataTypes.Contains(PlayerDataType.NAME))
            {
                session.CharacterName = PacketHelper.SafeReadString(reader, 64);
                session.IsMale = reader.GetBool();
                session.HairName = PacketHelper.SafeReadString(reader, 64);
                session.HairColorR = reader.GetFloat();
                session.HairColorG = reader.GetFloat();
                session.HairColorB = reader.GetFloat();
                session.SkinColorR = reader.GetFloat();
                session.SkinColorG = reader.GetFloat();
                session.SkinColorB = reader.GetFloat();

                // Read steam ID
                session.SteamId = reader.GetULong();

                // Check ban
                if (_server.Config.IsBanned(session.SteamId))
                {
                    ServerLogger.Info($"Banned player tried to connect: {session.CharacterName} ({session.SteamId})", "AUTH");
                    _network.DisconnectPlayer(session, "Banned");
                    return;
                }

                // Check whitelist
                if (!_server.Config.IsWhitelisted(session.SteamId))
                {
                    ServerLogger.Info($"Non-whitelisted player: {session.CharacterName} ({session.SteamId})", "AUTH");
                    _network.DisconnectPlayer(session, "Not on whitelist");
                    return;
                }

                session.IsModerator = _server.Config.IsModerator(session.SteamId);
                session.IsAdmin = _server.Config.IsAdmin(session.SteamId);
                session.IsAuthenticated = true;
                session.HasSentConnect = true;

                ServerLogger.Info($"Player identified: [{session.PlayerId}] {session.CharacterName} (Steam: {session.SteamId}){(session.IsModerator ? " [MOD]" : "")}{(session.IsAdmin ? " [ADMIN]" : "")}", "AUTH");

                // MOTD
                if (!string.IsNullOrWhiteSpace(_server.Config.MessageOfTheDay))
                {
                    _server.ChatManager.SendInfoMessage(session, _server.Config.MessageOfTheDay);
                }
            }

            if (dataTypes.Contains(PlayerDataType.LEVEL))
                session.Level = reader.GetInt();
            if (dataTypes.Contains(PlayerDataType.CLASS))
                session.Class = (PlayerClass)reader.GetByte();
            if (dataTypes.Contains(PlayerDataType.SCENE))
            {
                var newZone = PacketHelper.SafeReadString(reader, 64);
                _server.WorldManager.OnPlayerChangeZone(session, newZone);
            }

            // Relay connection packet to all other players
            RelayRawToAll(session, PacketType.PLAYER_CONNECT, reader);

            // Send existing player data to the new player
            _server.SendExistingPlayersTo(session);

            // Send player list update
            _network.SendPlayerList();
        }

        // ====================================================
        // PLAYER DATA
        // ====================================================
        private void HandlePlayerData(PlayerSession session, NetDataReader reader)
        {
            var entityId = reader.GetShort();
            var isSim = reader.GetBool();

            if (isSim)
            {
                RelayToZone(session, PacketType.PLAYER_DATA, reader, rewindFull: true);
                return;
            }

            if (entityId != session.PlayerId)
            {
                ServerLogger.Warning($"Player [{session.PlayerId}] sent data for wrong ID {entityId}", "PKT");
                return;
            }

            var flag = reader.GetUShort();
            var dataTypes = PacketHelper.ReadSubTypeFlag<PlayerDataType>(flag);

            // Parse and cache relevant data
            if (dataTypes.Contains(PlayerDataType.GEAR))
            {
                try
                {
                    var gearCount = reader.GetInt();
                    gearCount = Math.Clamp(gearCount, 0, 50); // Safety bound
                    session.Gear.Clear();
                    for (var i = 0; i < gearCount; i++)
                    {
                        var entry = new GearEntry
                        {
                            SlotType = reader.GetByte(),
                            ItemId = PacketHelper.SafeReadString(reader, 128),
                            Quality = reader.GetByte()
                        };
                        session.Gear.Add(entry);
                    }
                }
                catch (Exception ex)
                {
                    ServerLogger.Error($"Error reading gear data: {ex.Message}", "PKT");
                }
            }
            if (dataTypes.Contains(PlayerDataType.HEALTH))
            {
                session.Health = reader.GetInt();
                session.IsAlive = session.Health > 0;
            }
            if (dataTypes.Contains(PlayerDataType.MP))
                session.Mana = reader.GetInt();
            if (dataTypes.Contains(PlayerDataType.LEVEL))
                session.Level = reader.GetInt();
            if (dataTypes.Contains(PlayerDataType.CLASS))
                session.Class = (PlayerClass)reader.GetByte();
            if (dataTypes.Contains(PlayerDataType.SCENE))
            {
                var newZone = PacketHelper.SafeReadString(reader, 64);
                _server.WorldManager.OnPlayerChangeZone(session, newZone);
            }
            if (dataTypes.Contains(PlayerDataType.PERIODIC_UPDATE))
            {
                session.Health = reader.GetInt();
                session.MaxHealth = reader.GetInt();
                session.Mana = reader.GetInt();
                session.MaxMana = reader.GetInt();
                session.IsAlive = session.Health > 0;
            }

            // Relay to zone players
            RelayToZone(session, PacketType.PLAYER_DATA, reader, rewindFull: true);
        }

        // ====================================================
        // PLAYER TRANSFORM
        // ====================================================
        private void HandlePlayerTransform(PlayerSession session, NetDataReader reader)
        {
            var entityId = reader.GetShort();
            var isSim = reader.GetBool();
            short ownerID = -1;
            if (isSim)
                ownerID = reader.GetShort();

            if (!isSim && entityId != session.PlayerId)
            {
                ServerLogger.Debug($"Player [{session.PlayerId}] sent transform for wrong ID {entityId}", "PKT");
                return;
            }

            var flag = reader.GetUShort();
            var dataTypes = PacketHelper.ReadSubTypeFlag<PlayerDataType>(flag);

            Vec3 pos = session.Position;
            Quat rot = session.Rotation;

            if (dataTypes.Contains(PlayerDataType.POSITION))
                pos = PacketHelper.ReadVec3(reader);
            if (dataTypes.Contains(PlayerDataType.ROTATION))
                rot = PacketHelper.ReadQuat(reader);

            if (!isSim)
            {
                session.Position = pos;
                session.Rotation = rot;
                session.LastPositionUpdate = DateTime.UtcNow;
            }

            // Relay transform to zone players
            RelayPlayerTransformToZone(session, entityId, isSim, ownerID, dataTypes, pos, rot);
        }

        // ====================================================
        // PLAYER ACTION
        // ====================================================
        private void HandlePlayerAction(PlayerSession session, NetDataReader reader)
        {
            // Relay player action packets to zone players
            // The server trusts most action data from the zone owner,
            // but validates certain actions.
            RelayToZone(session, PacketType.PLAYER_ACTION, reader, rewindFull: true);
        }

        // ====================================================
        // PLAYER MESSAGE
        // ====================================================
        private void HandlePlayerMessage(PlayerSession session, NetDataReader reader)
        {
            var entityId = reader.GetShort();
            var messageType = (MessageType)reader.GetByte();
            var message = PacketHelper.SafeReadString(reader, 512);

            string target = null;
            short sender = 0;
            string color = null;
            bool append = false;
            bool isCombatLog = false;

            if (messageType == MessageType.WHISPER)
                target = PacketHelper.SafeReadString(reader, 64);
            if (messageType == MessageType.BATTLE_LOG)
            {
                sender = reader.GetShort();
                color = PacketHelper.SafeReadString(reader, 32);
                append = reader.GetBool();
                isCombatLog = reader.GetBool();
            }

            _server.ChatManager.HandleMessage(session, messageType, message, target, sender, color, append, isCombatLog);
        }

        // ====================================================
        // PLAYER REQUEST
        // ====================================================
        private void HandlePlayerRequest(PlayerSession session, NetDataReader reader)
        {
            var entityId = reader.GetShort();
            var flag = reader.GetUShort();
            var dataTypes = PacketHelper.ReadSubTypeFlag<RequestType>(flag);

            if (dataTypes.Contains(RequestType.ENTITY_ID))
            {
                var count = reader.GetInt();
                count = Math.Clamp(count, 0, 100);
                var entityTypes = new List<byte>();
                for (int i = 0; i < count; i++)
                    entityTypes.Add(reader.GetByte());

                // Allocate IDs server-side
                var ids = new List<short>();
                for (int i = 0; i < count; i++)
                    ids.Add(_server.WorldManager.AllocateEntityId());

                // Send response
                var writer = new NetDataWriter();
                writer.Put((byte)PacketType.SERVER_REQUEST);
                writer.Put(session.PlayerId);

                var respFlags = new HashSet<RequestType> { RequestType.ENTITY_ID };
                writer.Put(PacketHelper.GetSubTypeFlag(respFlags));

                writer.Put(ids.Count);
                foreach (var id in ids)
                    writer.Put(id);

                _network.SendTo(session, writer, DeliveryMethod.ReliableOrdered, PacketHelper.GetChannel(PacketType.SERVER_REQUEST));
            }

            if (dataTypes.Contains(RequestType.MOD_COMMAND))
            {
                var commandType = reader.GetByte();
                var playerName = PacketHelper.SafeReadString(reader, 64);
                _server.HandleModCommand(session, commandType, playerName);
            }

            if (dataTypes.Contains(RequestType.ENTITY_SPAWN))
            {
                var ownerID = reader.GetShort();
                var entityReqID = reader.GetShort();

                // Relay spawn request to the entity owner
                var ownerSession = _network.GetSession(ownerID);
                if (ownerSession != null)
                {
                    RelayRawTo(ownerSession, PacketType.PLAYER_REQUEST, reader);
                }
            }
        }

        // ====================================================
        // ENTITY PACKETS - Relayed from zone owners
        // ====================================================
        private void HandleEntityData(PlayerSession session, NetDataReader reader)
        {
            // Entity data comes from zone owners
            var targetCount = reader.GetInt();
            targetCount = Math.Clamp(targetCount, 0, 200);
            var targets = new List<short>();
            for (int i = 0; i < targetCount; i++)
                targets.Add(reader.GetShort());

            var entityId = reader.GetShort();
            var entityType = (EntityType)reader.GetByte();
            var zone = PacketHelper.SafeReadString(reader, 64);
            var flag = reader.GetUShort();
            var dataTypes = PacketHelper.ReadSubTypeFlag<EntityDataType>(flag);

            // Track entity health server-side
            if (dataTypes.Contains(EntityDataType.HEALTH))
            {
                var health = reader.GetInt();
                _server.WorldManager.UpdateEntityHealth(zone, entityId, health);
            }

            // Relay to target players
            RelayRawToTargets(targets, PacketType.ENTITY_DATA, reader);
        }

        private void HandleEntitySpawn(PlayerSession session, NetDataReader reader)
        {
            // Entity spawn from zone owner - record and relay
            var targetCount = reader.GetInt();
            targetCount = Math.Clamp(targetCount, 0, 200);
            var targets = new List<short>();
            for (int i = 0; i < targetCount; i++)
                targets.Add(reader.GetShort());

            var zone = PacketHelper.SafeReadString(reader, 64);
            var entityType = (EntityType)reader.GetByte();
            var spawnCount = reader.GetInt();
            spawnCount = Math.Clamp(spawnCount, 0, 500);

            // Record spawn data server-side
            for (int i = 0; i < spawnCount; i++)
            {
                try
                {
                    var spawnEntityId = reader.GetShort();
                    var npcId = PacketHelper.SafeReadString(reader, 128);
                    var spawnerId = reader.GetInt();
                    var isRare = reader.GetBool();
                    var pos = PacketHelper.ReadVec3(reader);
                    var rot = PacketHelper.ReadQuat(reader);
                    var spawnEntType = (EntityType)reader.GetByte();
                    var spawnZone = PacketHelper.SafeReadString(reader, 64);
                    var maxHP = reader.GetInt();
                    var syncStats = reader.GetBool();

                    short ownerIdPet = -1;
                    if (spawnEntType == EntityType.PET)
                        ownerIdPet = reader.GetShort();

                    int level = 0, baseAC = 0, baseHP = 0, baseMR = 0, basePR = 0, baseVR = 0, baseER = 0, baseDMG = 0;
                    float mhatkDelay = 0;
                    if (syncStats)
                    {
                        level = reader.GetInt();
                        baseAC = reader.GetInt();
                        baseHP = reader.GetInt();
                        baseMR = reader.GetInt();
                        basePR = reader.GetInt();
                        baseVR = reader.GetInt();
                        baseER = reader.GetInt();
                        baseDMG = reader.GetInt();
                        mhatkDelay = reader.GetFloat();
                    }

                    _server.WorldManager.RegisterEntity(zone, spawnEntityId, npcId, spawnerId, isRare, pos, rot, spawnEntType, maxHP);
                }
                catch (Exception ex)
                {
                    ServerLogger.Error($"Error reading spawn data: {ex.Message}", "PKT");
                    break;
                }
            }

            // Relay to targets
            RelayRawToTargets(targets, PacketType.ENTITY_SPAWN, reader);
        }

        private void HandleEntityTransform(PlayerSession session, NetDataReader reader)
        {
            // Entity transform - relay to targets
            var targetCount = reader.GetInt();
            targetCount = Math.Clamp(targetCount, 0, 200);
            var targets = new List<short>();
            for (int i = 0; i < targetCount; i++)
                targets.Add(reader.GetShort());

            var entityId = reader.GetShort();
            var zone = PacketHelper.SafeReadString(reader, 64);
            var flag = reader.GetUShort();
            var dataTypes = PacketHelper.ReadSubTypeFlag<EntityDataType>(flag);
            var entityType = (EntityType)reader.GetByte();

            Vec3 pos = Vec3.Zero;
            Quat rot = Quat.Identity;

            if (dataTypes.Contains(EntityDataType.POSITION))
                pos = PacketHelper.ReadVec3(reader);
            if (dataTypes.Contains(EntityDataType.ROTATION))
                rot = PacketHelper.ReadQuat(reader);

            // Update server-side entity position
            _server.WorldManager.UpdateEntityPosition(zone, entityId, pos, rot);

            // Relay to targets
            RelayEntityTransformToTargets(targets, entityId, zone, dataTypes, entityType, pos, rot);
        }

        private void HandleEntityAction(PlayerSession session, NetDataReader reader)
        {
            // Entity action - relay to targets
            RelayRawEntityToTargets(PacketType.ENTITY_ACTION, reader);
        }

        // ====================================================
        // GROUP
        // ====================================================
        private void HandleGroupPacket(PlayerSession session, NetDataReader reader)
        {
            _server.GroupManager.HandleGroupPacket(session, reader);
        }

        // ====================================================
        // ITEM DROP
        // ====================================================
        private void HandleItemDrop(PlayerSession session, NetDataReader reader)
        {
            _server.ItemDropManager.HandleItemDrop(session, reader);
        }

        // ====================================================
        // WEATHER
        // ====================================================
        private void HandleWeatherData(PlayerSession session, NetDataReader reader)
        {
            // Weather data from zone owner - relay to zone players
            RelayRawEntityToTargets(PacketType.WEATHER_DATA, reader);
        }

        // ====================================================
        // RELAY HELPERS
        // ====================================================

        /// <summary>
        /// Re-encodes and relays a player transform packet to all zone players.
        /// </summary>
        private void RelayPlayerTransformToZone(PlayerSession session, short entityId, bool isSim, short ownerID, HashSet<PlayerDataType> dataTypes, Vec3 pos, Quat rot)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.PLAYER_TRANSFORM);
            writer.Put(entityId);
            writer.Put(isSim);
            if (isSim)
                writer.Put(ownerID);
            writer.Put(PacketHelper.GetSubTypeFlag(dataTypes));

            if (dataTypes.Contains(PlayerDataType.POSITION))
                PacketHelper.PutVec3(writer, pos);
            if (dataTypes.Contains(PlayerDataType.ROTATION))
                PacketHelper.PutQuat(writer, rot);

            _network.BroadcastToZone(session.Zone, writer, DeliveryMethod.Unreliable,
                PacketHelper.GetChannel(PacketType.PLAYER_TRANSFORM), session.PlayerId);
        }

        /// <summary>
        /// Re-encodes and relays an entity transform packet to target players.
        /// </summary>
        private void RelayEntityTransformToTargets(List<short> targets, short entityId, string zone,
            HashSet<EntityDataType> dataTypes, EntityType entityType, Vec3 pos, Quat rot)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.ENTITY_TRANSFORM);
            writer.Put(targets.Count);
            foreach (var t in targets)
                writer.Put(t);

            writer.Put(entityId);
            writer.Put(zone);
            writer.Put(PacketHelper.GetSubTypeFlag(dataTypes));
            writer.Put((byte)entityType);

            if (dataTypes.Contains(EntityDataType.POSITION))
                PacketHelper.PutVec3(writer, pos);
            if (dataTypes.Contains(EntityDataType.ROTATION))
                PacketHelper.PutQuat(writer, rot);

            _network.BroadcastToPlayers(targets, writer, DeliveryMethod.Unreliable,
                PacketHelper.GetChannel(PacketType.ENTITY_TRANSFORM));
        }

        /// <summary>
        /// Relay a raw packet to all players in sender's zone (excluding sender).
        /// Falls back to full-buffer relay from original reader position.
        /// </summary>
        private void RelayToZone(PlayerSession session, PacketType type, NetDataReader reader, bool rewindFull = false)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)type);
            writer.Put(reader.RawData, 1, reader.RawDataSize - 1); // Skip the packet type byte we already read

            _network.BroadcastToZone(session.Zone, writer,
                PacketHelper.GetDeliveryMethod(type),
                PacketHelper.GetChannel(type),
                session.PlayerId);
        }

        /// <summary>
        /// Relay raw packet to all connected players (excluding sender).
        /// </summary>
        private void RelayRawToAll(PlayerSession session, PacketType type, NetDataReader reader)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)type);
            writer.Put(reader.RawData, 1, reader.RawDataSize - 1);

            _network.Broadcast(writer,
                PacketHelper.GetDeliveryMethod(type),
                PacketHelper.GetChannel(type),
                session.PlayerId);
        }

        /// <summary>
        /// Relay raw packet to a specific player.
        /// </summary>
        private void RelayRawTo(PlayerSession target, PacketType type, NetDataReader reader)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)type);
            writer.Put(reader.RawData, 1, reader.RawDataSize - 1);

            _network.SendTo(target, writer,
                PacketHelper.GetDeliveryMethod(type),
                PacketHelper.GetChannel(type));
        }

        /// <summary>
        /// Relay raw entity packet to target players embedded in the packet.
        /// Reads the target list from the front of the packet data.
        /// </summary>
        private void RelayRawEntityToTargets(PacketType type, NetDataReader reader)
        {
            // Entity packets have target list at the front (after packet type which was already read)
            var writer = new NetDataWriter();
            writer.Put((byte)type);
            writer.Put(reader.RawData, 1, reader.RawDataSize - 1);

            // Parse target list from raw data
            var targetCount = BitConverter.ToInt32(reader.RawData, 1);
            targetCount = Math.Clamp(targetCount, 0, 200);
            var targets = new List<short>();
            for (int i = 0; i < targetCount; i++)
            {
                var offset = 5 + i * 2;
                if (offset + 2 <= reader.RawDataSize)
                    targets.Add(BitConverter.ToInt16(reader.RawData, offset));
            }

            _network.BroadcastToPlayers(targets, writer,
                PacketHelper.GetDeliveryMethod(type),
                PacketHelper.GetChannel(type));
        }

        /// <summary>
        /// Relay raw entity data to a pre-parsed target list.
        /// </summary>
        private void RelayRawToTargets(List<short> targets, PacketType type, NetDataReader reader)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)type);
            writer.Put(reader.RawData, 1, reader.RawDataSize - 1);

            _network.BroadcastToPlayers(targets, writer,
                PacketHelper.GetDeliveryMethod(type),
                PacketHelper.GetChannel(type));
        }
    }
}

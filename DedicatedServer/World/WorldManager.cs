using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ErenshorDedicatedServer.Configuration;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;
using ErenshorDedicatedServer.Network;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ErenshorDedicatedServer.World
{
    /// <summary>
    /// Manages all world state: zones, entities, spawn tracking, zone ownership,
    /// and entity ID allocation. Acts as the authoritative source of truth for
    /// the game world on the dedicated server.
    /// </summary>
    public class WorldManager
    {
        private readonly ServerConfig _config;
        private readonly NetworkManager _network;

        // Zone state
        private readonly ConcurrentDictionary<string, ZoneState> _zones = new();

        // Global entity registry
        private readonly ConcurrentDictionary<short, ServerEntity> _allEntities = new();
        private readonly object _entityIdLock = new object();
        private short _nextEntityId = 100; // Reserve 1-99 for players

        // Zone ownership
        private readonly ConcurrentDictionary<string, short> _zoneOwners = new();
        private readonly ConcurrentDictionary<string, List<short>> _zoneMembers = new();

        // Events
        public event Action<PlayerSession, string, string> OnPlayerZoneChanged;

        public IReadOnlyDictionary<string, ZoneState> Zones => _zones;

        public WorldManager(ServerConfig config, NetworkManager network)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _network = network ?? throw new ArgumentNullException(nameof(network));

            InitializeZones();
        }

        private void InitializeZones()
        {
            if (_config.Zones == null || _config.Zones.Count == 0)
            {
                ServerLogger.Warning("No zones configured. Using default zone list.", "WORLD");
                _config.Zones = ZoneConfig.GetDefaultZones();
            }

            foreach (var zone in _config.Zones)
            {
                if (string.IsNullOrWhiteSpace(zone.Name)) continue;

                _zones[zone.Name] = new ZoneState
                {
                    Name = zone.Name,
                    DisplayName = zone.DisplayName ?? zone.Name,
                    MaxNpcs = zone.MaxNpcs,
                };
                _zoneMembers[zone.Name] = new List<short>();

                ServerLogger.Debug($"Initialized zone: {zone.DisplayName ?? zone.Name}", "WORLD");
            }

            ServerLogger.Info($"Initialized {_zones.Count} zones.", "WORLD");
        }

        // ====================================================
        // ENTITY ID ALLOCATION
        // ====================================================

        /// <summary>
        /// Allocates a unique entity ID for NPCs, sims, pets, etc.
        /// Thread-safe with overflow protection.
        /// </summary>
        public short AllocateEntityId()
        {
            lock (_entityIdLock)
            {
                // Scan for a free ID
                for (int attempts = 0; attempts < short.MaxValue - 100; attempts++)
                {
                    if (_nextEntityId < 100 || _nextEntityId >= short.MaxValue)
                        _nextEntityId = 100;

                    var id = _nextEntityId++;
                    if (!_allEntities.ContainsKey(id) && !_network.Players.ContainsKey(id))
                        return id;
                }

                ServerLogger.Error("Entity ID space exhausted!", "WORLD");
                return -1;
            }
        }

        // ====================================================
        // ENTITY MANAGEMENT
        // ====================================================

        public void RegisterEntity(string zone, short entityId, string npcId, int spawnerId,
            bool isRare, Vec3 position, Quat rotation, EntityType type, int maxHP)
        {
            if (string.IsNullOrEmpty(zone)) return;

            var entity = new ServerEntity
            {
                EntityId = entityId,
                NpcId = npcId ?? "",
                SpawnerId = spawnerId,
                IsRare = isRare,
                Position = position,
                Rotation = rotation,
                EntityType = type,
                Zone = zone,
                Health = maxHP,
                MaxHealth = maxHP,
                IsAlive = maxHP > 0,
                SpawnTime = DateTime.UtcNow
            };

            _allEntities[entityId] = entity;

            if (_zones.TryGetValue(zone, out var zoneState))
            {
                zoneState.Entities[entityId] = entity;
            }

            ServerLogger.Debug($"Entity registered: [{entityId}] {npcId} in {zone} HP:{maxHP}", "WORLD");
        }

        public void UnregisterEntity(short entityId)
        {
            if (_allEntities.TryRemove(entityId, out var entity))
            {
                if (_zones.TryGetValue(entity.Zone, out var zoneState))
                {
                    zoneState.Entities.TryRemove(entityId, out _);
                }
            }
        }

        public ServerEntity GetEntity(short entityId)
        {
            _allEntities.TryGetValue(entityId, out var entity);
            return entity;
        }

        public void UpdateEntityHealth(string zone, short entityId, int health)
        {
            if (_allEntities.TryGetValue(entityId, out var entity))
            {
                entity.Health = health;
                entity.IsAlive = health > 0;

                if (!entity.IsAlive)
                {
                    entity.DeathTime = DateTime.UtcNow;
                    ServerLogger.Debug($"Entity [{entityId}] {entity.NpcId} died in {zone}", "WORLD");
                }
            }
        }

        public void UpdateEntityPosition(string zone, short entityId, Vec3 pos, Quat rot)
        {
            if (_allEntities.TryGetValue(entityId, out var entity))
            {
                entity.Position = pos;
                entity.Rotation = rot;
            }
        }

        // ====================================================
        // ZONE MANAGEMENT
        // ====================================================

        public void OnPlayerChangeZone(PlayerSession session, string newZone)
        {
            if (session == null || string.IsNullOrEmpty(newZone)) return;

            var prevZone = session.Zone;
            if (prevZone == newZone) return;

            session.PreviousZone = prevZone;
            session.Zone = newZone;

            // Remove from old zone
            if (!string.IsNullOrEmpty(prevZone))
            {
                lock (_zoneMembers)
                {
                    if (_zoneMembers.TryGetValue(prevZone, out var oldList))
                    {
                        oldList.Remove(session.PlayerId);

                        // If old zone owner left, reassign
                        if (_zoneOwners.TryGetValue(prevZone, out var oldOwner) && oldOwner == session.PlayerId)
                        {
                            if (oldList.Count > 0)
                            {
                                var newOwner = oldList[0];
                                _zoneOwners[prevZone] = newOwner;
                                SendZoneOwnership(newOwner, prevZone, oldList);
                                ServerLogger.Info($"Zone {prevZone} ownership transferred to [{newOwner}]", "ZONE");
                            }
                            else
                            {
                                _zoneOwners.TryRemove(prevZone, out _);
                                // Clean up zone entities when empty
                                if (_zones.TryGetValue(prevZone, out var prevZoneState))
                                {
                                    prevZoneState.Entities.Clear();
                                    prevZoneState.IsServerPopulated = false;
                                    ServerLogger.Debug($"Zone {prevZone} now empty, cleared entities", "ZONE");
                                }
                            }
                        }
                    }
                }
            }

            // Add to new zone
            lock (_zoneMembers)
            {
                if (!_zoneMembers.TryGetValue(newZone, out var newList))
                {
                    newList = new List<short>();
                    _zoneMembers[newZone] = newList;
                }

                if (!newList.Contains(session.PlayerId))
                    newList.Add(session.PlayerId);

                // Assign zone ownership
                if (!_zoneOwners.ContainsKey(newZone) || newList.Count == 1)
                {
                    _zoneOwners[newZone] = session.PlayerId;
                    ServerLogger.Info($"Zone {newZone} owner: [{session.PlayerId}] {session.CharacterName}", "ZONE");
                }

                // Send zone ownership to the player
                SendZoneOwnership(session.PlayerId, newZone, newList);
            }

            ServerLogger.Info($"[{session.PlayerId}] {session.CharacterName} moved {prevZone ?? "null"} -> {newZone}", "ZONE");

            OnPlayerZoneChanged?.Invoke(session, newZone, prevZone);
        }

        public void OnPlayerDisconnect(PlayerSession session)
        {
            if (session == null) return;

            var zone = session.Zone;
            if (string.IsNullOrEmpty(zone)) return;

            lock (_zoneMembers)
            {
                if (_zoneMembers.TryGetValue(zone, out var list))
                {
                    list.Remove(session.PlayerId);

                    if (_zoneOwners.TryGetValue(zone, out var owner) && owner == session.PlayerId)
                    {
                        if (list.Count > 0)
                        {
                            var newOwner = list[0];
                            _zoneOwners[zone] = newOwner;
                            SendZoneOwnership(newOwner, zone, list);
                            ServerLogger.Info($"Zone {zone} ownership transferred to [{newOwner}] (disconnect)", "ZONE");
                        }
                        else
                        {
                            _zoneOwners.TryRemove(zone, out _);
                        }
                    }
                }
            }

            // Clean up player's entities
            foreach (var entity in _allEntities.Values.ToArray())
            {
                if (entity.OwnerId == session.PlayerId)
                    UnregisterEntity(entity.EntityId);
            }
        }

        public short GetZoneOwner(string zone)
        {
            _zoneOwners.TryGetValue(zone, out var owner);
            return owner;
        }

        public List<short> GetZoneMembers(string zone)
        {
            if (string.IsNullOrEmpty(zone)) return new List<short>();
            lock (_zoneMembers)
            {
                return _zoneMembers.TryGetValue(zone, out var list) ? new List<short>(list) : new List<short>();
            }
        }

        public bool IsZoneOwner(short playerId, string zone)
        {
            return _zoneOwners.TryGetValue(zone, out var owner) && owner == playerId;
        }

        // ====================================================
        // ZONE OWNERSHIP PACKETS
        // ====================================================

        private void SendZoneOwnership(short playerId, string zone, List<short> playerList)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.SERVER_INFO);

            var flags = new HashSet<ServerInfoType> { ServerInfoType.ZONE_OWNERSHIP };
            writer.Put(PacketHelper.GetSubTypeFlag(flags));

            writer.Put(_zoneOwners.TryGetValue(zone, out var ownerId) ? ownerId : playerId);
            writer.Put(zone);
            writer.Put(playerList.Count);
            foreach (var p in playerList)
                writer.Put(p);

            _network.SendTo(playerId, writer, DeliveryMethod.ReliableOrdered, PacketHelper.GetChannel(PacketType.SERVER_INFO));
        }

        /// <summary>
        /// Broadcast zone ownership to all players in a zone.
        /// </summary>
        public void BroadcastZoneOwnership(string zone)
        {
            if (!_zoneOwners.TryGetValue(zone, out var owner)) return;

            lock (_zoneMembers)
            {
                if (!_zoneMembers.TryGetValue(zone, out var members)) return;

                foreach (var memberId in members)
                {
                    SendZoneOwnership(memberId, zone, members);
                }
            }
        }

        // ====================================================
        // TICK
        // ====================================================

        public void Tick(float deltaTime)
        {
            // Clean up dead entities that have been dead long enough
            var now = DateTime.UtcNow;
            foreach (var entity in _allEntities.Values.ToArray())
            {
                if (!entity.IsAlive && entity.DeathTime.HasValue)
                {
                    if ((now - entity.DeathTime.Value).TotalSeconds > 30)
                    {
                        UnregisterEntity(entity.EntityId);
                    }
                }
            }
        }

        // ====================================================
        // INFO / STATS
        // ====================================================

        public int GetTotalEntityCount() => _allEntities.Count;
        public int GetAliveEntityCount() => _allEntities.Values.Count(e => e.IsAlive);

        public Dictionary<string, int> GetZonePlayerCounts()
        {
            var result = new Dictionary<string, int>();
            lock (_zoneMembers)
            {
                foreach (var kvp in _zoneMembers)
                {
                    if (kvp.Value.Count > 0)
                        result[kvp.Key] = kvp.Value.Count;
                }
            }
            return result;
        }

        public Dictionary<string, (int players, int entities, short owner)> GetZoneStats()
        {
            var result = new Dictionary<string, (int, int, short)>();
            lock (_zoneMembers)
            {
                foreach (var kvp in _zoneMembers)
                {
                    if (kvp.Value.Count > 0)
                    {
                        var entityCount = _zones.TryGetValue(kvp.Key, out var zs) ? zs.Entities.Count : 0;
                        var owner = _zoneOwners.TryGetValue(kvp.Key, out var o) ? o : (short)-1;
                        result[kvp.Key] = (kvp.Value.Count, entityCount, owner);
                    }
                }
            }
            return result;
        }
    }

    /// <summary>
    /// State tracked per zone.
    /// </summary>
    public class ZoneState
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public int MaxNpcs { get; set; }
        public ConcurrentDictionary<short, ServerEntity> Entities { get; } = new();

        /// <summary>
        /// Whether the server has sent authoritative spawn commands for this zone.
        /// Reset when zone empties. Used to prevent duplicate population.
        /// </summary>
        public bool IsServerPopulated { get; set; }
    }

    /// <summary>
    /// Server-side representation of a game entity (NPC, mob, pet, sim).
    /// </summary>
    public class ServerEntity
    {
        public short EntityId { get; set; }
        public string NpcId { get; set; } = "";
        public int SpawnerId { get; set; }
        public bool IsRare { get; set; }
        public Vec3 Position { get; set; }
        public Quat Rotation { get; set; }
        public EntityType EntityType { get; set; }
        public string Zone { get; set; } = "";
        public int Health { get; set; }
        public int MaxHealth { get; set; }
        public bool IsAlive { get; set; } = true;
        public short OwnerId { get; set; } = -1;
        public DateTime SpawnTime { get; set; }
        public DateTime? DeathTime { get; set; }

        // AI State
        public NpcState AiState { get; set; } = NpcState.Idle;
        public short TargetId { get; set; } = -1;
        public Vec3 SpawnPosition { get; set; }
        public float LastAttackTime { get; set; }
    }
}

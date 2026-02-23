using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;
using ErenshorDedicatedServer.Network;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ErenshorDedicatedServer.World
{
    /// <summary>
    /// Tracks spawn points and manages respawn timers for entities.
    /// When an entity dies, the spawn manager queues a respawn after the configured delay.
    /// The actual spawn command is sent to the zone owner who performs the client-side instantiation.
    /// </summary>
    public class SpawnManager
    {
        private readonly WorldManager _world;
        private readonly NetworkManager _network;

        /// <summary>
        /// All known spawn points, keyed by spawner ID.
        /// </summary>
        private readonly ConcurrentDictionary<int, SpawnPoint> _spawnPoints = new();

        /// <summary>
        /// Queue of pending respawns, sorted by respawn time.
        /// </summary>
        private readonly List<RespawnEntry> _respawnQueue = new();
        private readonly object _queueLock = new object();

        // Configuration
        private float _baseRespawnTime = 120f; // 2 minutes default
        private float _rareRespawnTime = 600f; // 10 minutes for rares
        private float _bossRespawnTime = 1800f; // 30 minutes for bosses

        public SpawnManager(WorldManager world, NetworkManager network)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _network = network ?? throw new ArgumentNullException(nameof(network));
        }

        /// <summary>
        /// Registers a spawn point from zone owner spawn data.
        /// </summary>
        public void RegisterSpawnPoint(string zone, int spawnerId, string npcId,
            Vec3 position, Quat rotation, EntityType entityType, bool isRare, int maxHP)
        {
            var sp = new SpawnPoint
            {
                SpawnerId = spawnerId,
                Zone = zone,
                NpcId = npcId,
                Position = position,
                Rotation = rotation,
                EntityType = entityType,
                IsRare = isRare,
                MaxHP = maxHP,
                IsActive = true,
                LastSpawnTime = DateTime.UtcNow
            };

            _spawnPoints[spawnerId] = sp;
            ServerLogger.Debug($"Spawn point registered: [{spawnerId}] {npcId} in {zone}", "SPAWN");
        }

        /// <summary>
        /// Called when an entity dies. Queues a respawn if it has a valid spawn point.
        /// </summary>
        public void OnEntityDeath(short entityId, int spawnerId, string zone)
        {
            if (spawnerId <= 0) return;

            if (!_spawnPoints.TryGetValue(spawnerId, out var sp))
                return;

            if (!sp.IsActive) return;

            var delay = GetRespawnDelay(sp);
            var respawnTime = DateTime.UtcNow.AddSeconds(delay);

            lock (_queueLock)
            {
                // Don't queue duplicate respawns for the same spawner
                if (_respawnQueue.Any(r => r.SpawnerId == spawnerId))
                    return;

                _respawnQueue.Add(new RespawnEntry
                {
                    SpawnerId = spawnerId,
                    Zone = zone,
                    RespawnTime = respawnTime,
                    SpawnPoint = sp
                });

                // Keep sorted by respawn time for efficient processing
                _respawnQueue.Sort((a, b) => a.RespawnTime.CompareTo(b.RespawnTime));
            }

            ServerLogger.Debug($"Queued respawn for [{spawnerId}] {sp.NpcId} in {zone} (in {delay:F0}s)", "SPAWN");
        }

        /// <summary>
        /// Tick: process respawn queue and notify zone owners of pending respawns.
        /// </summary>
        public void Tick(float deltaTime)
        {
            var now = DateTime.UtcNow;
            List<RespawnEntry> readyEntries = null;

            lock (_queueLock)
            {
                while (_respawnQueue.Count > 0 && _respawnQueue[0].RespawnTime <= now)
                {
                    if (readyEntries == null)
                        readyEntries = new List<RespawnEntry>();
                    readyEntries.Add(_respawnQueue[0]);
                    _respawnQueue.RemoveAt(0);
                }
            }

            if (readyEntries == null) return;

            foreach (var entry in readyEntries)
            {
                ProcessRespawn(entry);
            }
        }

        private void ProcessRespawn(RespawnEntry entry)
        {
            // Find the zone owner
            var ownerId = _world.GetZoneOwner(entry.Zone);
            if (ownerId <= 0)
            {
                // No zone owner - zone is empty, no need to respawn
                ServerLogger.Debug($"No zone owner for {entry.Zone}, skipping respawn [{entry.SpawnerId}]", "SPAWN");
                return;
            }

            var ownerSession = _network.GetSession(ownerId);
            if (ownerSession == null) return;

            // Allocate an entity ID for the new spawn
            var newEntityId = _world.AllocateEntityId();
            if (newEntityId < 0)
            {
                ServerLogger.Warning("Cannot respawn: entity ID space exhausted", "SPAWN");
                return;
            }

            // Send respawn command to zone owner
            SendRespawnCommand(ownerSession, entry, newEntityId);

            // Update spawn point
            entry.SpawnPoint.LastSpawnTime = DateTime.UtcNow;
            entry.SpawnPoint.CurrentEntityId = newEntityId;

            ServerLogger.Debug($"Respawn triggered: [{entry.SpawnerId}] {entry.SpawnPoint.NpcId} as entity [{newEntityId}] in {entry.Zone}", "SPAWN");
        }

        private void SendRespawnCommand(PlayerSession owner, RespawnEntry entry, short entityId)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.SERVER_REQUEST);
            writer.Put(owner.PlayerId);

            var flags = new HashSet<RequestType> { RequestType.ENTITY_SPAWN };
            writer.Put(PacketHelper.GetSubTypeFlag(flags));

            // Respawn data
            writer.Put(entry.SpawnerId);
            writer.Put(entityId);
            writer.Put(entry.SpawnPoint.NpcId);
            PacketHelper.PutVec3(writer, entry.SpawnPoint.Position);
            PacketHelper.PutQuat(writer, entry.SpawnPoint.Rotation);
            writer.Put((byte)entry.SpawnPoint.EntityType);
            writer.Put(entry.SpawnPoint.MaxHP);

            _network.SendTo(owner, writer, DeliveryMethod.ReliableOrdered,
                PacketHelper.GetChannel(PacketType.SERVER_REQUEST));
        }

        private float GetRespawnDelay(SpawnPoint sp)
        {
            // Boss spawns (negative custom spawn IDs)
            if (sp.SpawnerId < 0)
                return _bossRespawnTime;

            if (sp.IsRare)
                return _rareRespawnTime;

            return _baseRespawnTime;
        }

        /// <summary>
        /// Clears spawn points and respawn queue for a zone.
        /// Called when a zone becomes empty and entities are cleaned up.
        /// </summary>
        public void ClearZone(string zone)
        {
            var toRemove = _spawnPoints.Values.Where(sp => sp.Zone == zone).Select(sp => sp.SpawnerId).ToList();
            foreach (var id in toRemove)
            {
                _spawnPoints.TryRemove(id, out _);
            }

            lock (_queueLock)
            {
                _respawnQueue.RemoveAll(r => r.Zone == zone);
            }

            ServerLogger.Debug($"Cleared spawn data for zone {zone}", "SPAWN");
        }

        /// <summary>
        /// Gets the number of pending respawns.
        /// </summary>
        public int GetPendingRespawnCount()
        {
            lock (_queueLock) { return _respawnQueue.Count; }
        }

        /// <summary>
        /// Gets the total number of tracked spawn points.
        /// </summary>
        public int GetSpawnPointCount() => _spawnPoints.Count;

        /// <summary>
        /// Sets respawn timer configuration.
        /// </summary>
        public void SetRespawnTimes(float baseTime, float rareTime, float bossTime)
        {
            _baseRespawnTime = Math.Max(10f, baseTime);
            _rareRespawnTime = Math.Max(30f, rareTime);
            _bossRespawnTime = Math.Max(60f, bossTime);
        }
    }

    public class SpawnPoint
    {
        public int SpawnerId { get; set; }
        public string Zone { get; set; } = "";
        public string NpcId { get; set; } = "";
        public Vec3 Position { get; set; }
        public Quat Rotation { get; set; }
        public EntityType EntityType { get; set; }
        public bool IsRare { get; set; }
        public int MaxHP { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime LastSpawnTime { get; set; }
        public short CurrentEntityId { get; set; } = -1;
    }

    public class RespawnEntry
    {
        public int SpawnerId { get; set; }
        public string Zone { get; set; } = "";
        public DateTime RespawnTime { get; set; }
        public SpawnPoint SpawnPoint { get; set; }
    }
}

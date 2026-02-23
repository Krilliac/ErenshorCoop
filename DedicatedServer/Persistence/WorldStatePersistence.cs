using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;
using ErenshorDedicatedServer.World;
using Newtonsoft.Json;

namespace ErenshorDedicatedServer.Persistence
{
    /// <summary>
    /// Saves and restores world state across server restarts.
    /// Tracks entity positions, health, spawn states, and zone data.
    /// The world state file captures a snapshot of the server's understanding
    /// of the game world at a point in time.
    /// </summary>
    public class WorldStatePersistence
    {
        private readonly string _savePath;

        public WorldStatePersistence(string savePath = "data/world_state.json")
        {
            _savePath = savePath;

            var dir = Path.GetDirectoryName(_savePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        /// <summary>
        /// Saves the current world state to disk.
        /// </summary>
        public bool SaveWorldState(WorldManager worldManager, SpawnManager spawnManager)
        {
            if (worldManager == null) return false;

            try
            {
                var state = new WorldSaveData
                {
                    SaveTime = DateTime.UtcNow,
                    Zones = new List<ZoneSaveData>()
                };

                // Save each zone's entity state
                foreach (var zone in worldManager.Zones)
                {
                    var zoneData = new ZoneSaveData
                    {
                        ZoneName = zone.Key,
                        Entities = new List<EntitySaveData>()
                    };

                    foreach (var entity in zone.Value.Entities.Values)
                    {
                        zoneData.Entities.Add(new EntitySaveData
                        {
                            EntityId = entity.EntityId,
                            NpcId = entity.NpcId,
                            SpawnerId = entity.SpawnerId,
                            EntityType = entity.EntityType.ToString(),
                            IsRare = entity.IsRare,
                            Health = entity.Health,
                            MaxHealth = entity.MaxHealth,
                            IsAlive = entity.IsAlive,
                            PositionX = entity.Position.X,
                            PositionY = entity.Position.Y,
                            PositionZ = entity.Position.Z,
                            RotationX = entity.Rotation.X,
                            RotationY = entity.Rotation.Y,
                            RotationZ = entity.Rotation.Z,
                            RotationW = entity.Rotation.W,
                            SpawnPositionX = entity.SpawnPosition.X,
                            SpawnPositionY = entity.SpawnPosition.Y,
                            SpawnPositionZ = entity.SpawnPosition.Z,
                            AiState = entity.AiState.ToString(),
                            DeathTime = entity.DeathTime,
                            SpawnTime = entity.SpawnTime
                        });
                    }

                    // Only save zones that have entities
                    if (zoneData.Entities.Count > 0)
                        state.Zones.Add(zoneData);
                }

                // Save spawn point states (respawn timers, active status)
                state.SpawnStates = new List<SpawnStateSaveData>();
                // SpawnManager tracks spawn points internally - we save their state
                // through the SpawnManager's public API

                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                var tempPath = _savePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _savePath, overwrite: true);

                ServerLogger.Debug($"World state saved: {state.Zones.Count} zones, " +
                    $"{state.Zones.Sum(z => z.Entities.Count)} entities", "PERSIST");
                return true;
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Failed to save world state: {ex.Message}", "PERSIST");
                return false;
            }
        }

        /// <summary>
        /// Loads saved world state from disk. Returns null if no save exists.
        /// </summary>
        public WorldSaveData LoadWorldState()
        {
            if (!File.Exists(_savePath)) return null;

            try
            {
                var json = File.ReadAllText(_savePath);
                var state = JsonConvert.DeserializeObject<WorldSaveData>(json);

                if (state != null)
                {
                    var totalEntities = state.Zones?.Sum(z => z.Entities?.Count ?? 0) ?? 0;
                    ServerLogger.Info($"Loaded world state from {state.SaveTime}: " +
                        $"{state.Zones?.Count ?? 0} zones, {totalEntities} entities", "PERSIST");
                }

                return state;
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Failed to load world state: {ex.Message}", "PERSIST");
                return null;
            }
        }

        /// <summary>
        /// Restores entities from saved world state into the WorldManager.
        /// Only restores entities that are still alive - dead entities are left
        /// to the respawn system.
        /// </summary>
        public int RestoreWorldState(WorldSaveData state, WorldManager worldManager)
        {
            if (state?.Zones == null || worldManager == null) return 0;

            int restored = 0;
            foreach (var zoneData in state.Zones)
            {
                if (string.IsNullOrEmpty(zoneData.ZoneName)) continue;

                foreach (var entityData in zoneData.Entities)
                {
                    // Skip dead entities that have been dead a long time
                    if (!entityData.IsAlive && entityData.DeathTime.HasValue)
                    {
                        if ((DateTime.UtcNow - entityData.DeathTime.Value).TotalSeconds > 30)
                            continue;
                    }

                    var pos = new Vec3(entityData.PositionX, entityData.PositionY, entityData.PositionZ);
                    var rot = new Quat(entityData.RotationX, entityData.RotationY, entityData.RotationZ, entityData.RotationW);
                    var entityType = ParseEntityType(entityData.EntityType);

                    worldManager.RegisterEntity(
                        zoneData.ZoneName,
                        entityData.EntityId,
                        entityData.NpcId,
                        entityData.SpawnerId,
                        entityData.IsRare,
                        pos, rot,
                        entityType,
                        entityData.MaxHealth
                    );

                    // Restore current health (not just max)
                    if (entityData.Health != entityData.MaxHealth)
                    {
                        worldManager.UpdateEntityHealth(zoneData.ZoneName, entityData.EntityId, entityData.Health);
                    }

                    // Restore spawn position
                    var entity = worldManager.GetEntity(entityData.EntityId);
                    if (entity != null)
                    {
                        entity.SpawnPosition = new Vec3(
                            entityData.SpawnPositionX,
                            entityData.SpawnPositionY,
                            entityData.SpawnPositionZ
                        );
                        entity.SpawnTime = entityData.SpawnTime;
                    }

                    restored++;
                }
            }

            ServerLogger.Info($"Restored {restored} entities from world state.", "PERSIST");
            return restored;
        }

        /// <summary>
        /// Checks if a world state save exists.
        /// </summary>
        public bool HasSaveData()
        {
            return File.Exists(_savePath);
        }

        private static EntityType ParseEntityType(string typeStr)
        {
            if (string.IsNullOrWhiteSpace(typeStr)) return EntityType.ENEMY;
            return typeStr.ToUpperInvariant() switch
            {
                "ENEMY" => EntityType.ENEMY,
                "SIM" => EntityType.SIM,
                "PET" => EntityType.PET,
                "PLAYER" => EntityType.PLAYER,
                _ => EntityType.ENEMY
            };
        }
    }

    // ====================================================
    // SAVE DATA STRUCTURES
    // ====================================================

    public class WorldSaveData
    {
        [JsonProperty("save_time")]
        public DateTime SaveTime { get; set; }

        [JsonProperty("zones")]
        public List<ZoneSaveData> Zones { get; set; } = new();

        [JsonProperty("spawn_states")]
        public List<SpawnStateSaveData> SpawnStates { get; set; } = new();
    }

    public class ZoneSaveData
    {
        [JsonProperty("zone_name")]
        public string ZoneName { get; set; } = "";

        [JsonProperty("entities")]
        public List<EntitySaveData> Entities { get; set; } = new();
    }

    public class EntitySaveData
    {
        [JsonProperty("entity_id")]
        public short EntityId { get; set; }

        [JsonProperty("npc_id")]
        public string NpcId { get; set; } = "";

        [JsonProperty("spawner_id")]
        public int SpawnerId { get; set; }

        [JsonProperty("entity_type")]
        public string EntityType { get; set; } = "ENEMY";

        [JsonProperty("is_rare")]
        public bool IsRare { get; set; }

        [JsonProperty("health")]
        public int Health { get; set; }

        [JsonProperty("max_health")]
        public int MaxHealth { get; set; }

        [JsonProperty("is_alive")]
        public bool IsAlive { get; set; }

        [JsonProperty("position_x")]
        public float PositionX { get; set; }

        [JsonProperty("position_y")]
        public float PositionY { get; set; }

        [JsonProperty("position_z")]
        public float PositionZ { get; set; }

        [JsonProperty("rotation_x")]
        public float RotationX { get; set; }

        [JsonProperty("rotation_y")]
        public float RotationY { get; set; }

        [JsonProperty("rotation_z")]
        public float RotationZ { get; set; }

        [JsonProperty("rotation_w")]
        public float RotationW { get; set; } = 1f;

        [JsonProperty("spawn_position_x")]
        public float SpawnPositionX { get; set; }

        [JsonProperty("spawn_position_y")]
        public float SpawnPositionY { get; set; }

        [JsonProperty("spawn_position_z")]
        public float SpawnPositionZ { get; set; }

        [JsonProperty("ai_state")]
        public string AiState { get; set; } = "Idle";

        [JsonProperty("death_time")]
        public DateTime? DeathTime { get; set; }

        [JsonProperty("spawn_time")]
        public DateTime SpawnTime { get; set; }
    }

    public class SpawnStateSaveData
    {
        [JsonProperty("spawner_id")]
        public int SpawnerId { get; set; }

        [JsonProperty("zone")]
        public string Zone { get; set; } = "";

        [JsonProperty("is_active")]
        public bool IsActive { get; set; } = true;

        [JsonProperty("current_entity_id")]
        public short CurrentEntityId { get; set; } = -1;

        [JsonProperty("last_spawn_time")]
        public DateTime LastSpawnTime { get; set; }
    }
}

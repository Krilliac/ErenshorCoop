using System;
using System.Collections.Generic;
using System.IO;
using ErenshorDedicatedServer.Configuration;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;
using ErenshorDedicatedServer.World;
using Newtonsoft.Json;

namespace ErenshorDedicatedServer.Persistence
{
    /// <summary>
    /// Loads NPC and spawn point definitions from JSON data files.
    /// Provides the server with authoritative knowledge of what entities should exist
    /// in each zone, independent of client-reported data.
    ///
    /// Data sources (in priority order):
    /// 1. External spawn definitions file (data/spawns.json)
    /// 2. Zone-embedded spawn points from server config
    /// 3. Client-reported spawns (fallback when no server-side data exists)
    /// </summary>
    public class SpawnDefinitionLoader
    {
        private readonly ServerConfig _config;

        /// <summary>
        /// All loaded NPC definitions, keyed by NPC ID string.
        /// </summary>
        public Dictionary<string, NpcDefinition> NpcDefinitions { get; } = new();

        /// <summary>
        /// All loaded spawn definitions, keyed by zone name -> list of spawn defs.
        /// </summary>
        public Dictionary<string, List<SpawnDefinition>> SpawnDefinitions { get; } = new();

        /// <summary>
        /// Whether any spawn data was loaded from files.
        /// When false, the server falls back to client-reported spawns.
        /// </summary>
        public bool HasLoadedData { get; private set; }

        public SpawnDefinitionLoader(ServerConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Loads all definitions. Call this on server startup.
        /// </summary>
        public void LoadAll()
        {
            LoadNpcDefinitions();
            LoadSpawnDefinitions();
            LoadConfigSpawnPoints();

            if (HasLoadedData)
            {
                var totalSpawns = 0;
                foreach (var zone in SpawnDefinitions.Values)
                    totalSpawns += zone.Count;

                ServerLogger.Info($"Loaded {NpcDefinitions.Count} NPC definitions, {totalSpawns} spawn points across {SpawnDefinitions.Count} zones.", "SPAWN");
            }
            else
            {
                ServerLogger.Info("No spawn definition files found. Server will use client-reported spawn data.", "SPAWN");
            }
        }

        /// <summary>
        /// Loads NPC definitions from the configured path.
        /// </summary>
        private void LoadNpcDefinitions()
        {
            var path = _config.NpcDefinitionsPath;
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                if (!File.Exists(path))
                {
                    ServerLogger.Debug($"NPC definitions file not found: {path}", "SPAWN");
                    // Create a template file
                    CreateTemplateNpcFile(path);
                    return;
                }

                var json = File.ReadAllText(path);
                var defs = JsonConvert.DeserializeObject<NpcDefinitionFile>(json);
                if (defs?.Npcs == null) return;

                foreach (var npc in defs.Npcs)
                {
                    if (string.IsNullOrWhiteSpace(npc.NpcId)) continue;
                    NpcDefinitions[npc.NpcId] = npc;
                }

                HasLoadedData = true;
                ServerLogger.Info($"Loaded {NpcDefinitions.Count} NPC definitions from {path}", "SPAWN");
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Failed to load NPC definitions from {path}: {ex.Message}", "SPAWN");
            }
        }

        /// <summary>
        /// Loads spawn point definitions from the configured path.
        /// </summary>
        private void LoadSpawnDefinitions()
        {
            var path = _config.SpawnDefinitionsPath;
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                if (!File.Exists(path))
                {
                    ServerLogger.Debug($"Spawn definitions file not found: {path}", "SPAWN");
                    // Create a template file
                    CreateTemplateSpawnFile(path);
                    return;
                }

                var json = File.ReadAllText(path);
                var file = JsonConvert.DeserializeObject<SpawnDefinitionFile>(json);
                if (file?.Zones == null) return;

                foreach (var zoneDef in file.Zones)
                {
                    if (string.IsNullOrWhiteSpace(zoneDef.ZoneName)) continue;
                    if (zoneDef.Spawns == null || zoneDef.Spawns.Count == 0) continue;

                    SpawnDefinitions[zoneDef.ZoneName] = zoneDef.Spawns;
                }

                HasLoadedData = true;
                ServerLogger.Info($"Loaded spawn definitions for {SpawnDefinitions.Count} zones from {path}", "SPAWN");
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Failed to load spawn definitions from {path}: {ex.Message}", "SPAWN");
            }
        }

        /// <summary>
        /// Also loads any spawn points embedded in the server config's zone definitions.
        /// These supplement (don't replace) file-based definitions.
        /// </summary>
        private void LoadConfigSpawnPoints()
        {
            if (_config.Zones == null) return;

            foreach (var zone in _config.Zones)
            {
                if (zone.SpawnPoints == null || zone.SpawnPoints.Count == 0) continue;

                if (!SpawnDefinitions.ContainsKey(zone.Name))
                    SpawnDefinitions[zone.Name] = new List<SpawnDefinition>();

                foreach (var sp in zone.SpawnPoints)
                {
                    if (string.IsNullOrWhiteSpace(sp.NpcId)) continue;

                    SpawnDefinitions[zone.Name].Add(new SpawnDefinition
                    {
                        SpawnerId = sp.Id,
                        NpcId = sp.NpcId,
                        Position = new float[] { sp.Position[0], sp.Position[1], sp.Position[2] },
                        Rotation = new float[] { sp.Rotation[0], sp.Rotation[1], sp.Rotation[2], sp.Rotation[3] },
                        RespawnTimeSeconds = sp.RespawnTime,
                        IsRare = sp.IsRare,
                        WanderRange = sp.WanderRange,
                        EntityType = "ENEMY"
                    });
                }

                if (SpawnDefinitions[zone.Name].Count > 0)
                    HasLoadedData = true;
            }
        }

        /// <summary>
        /// Registers all loaded spawn definitions with the SpawnManager.
        /// Call this after SpawnManager is initialized.
        /// </summary>
        public void RegisterSpawnsWithManager(SpawnManager spawnManager)
        {
            if (!HasLoadedData) return;

            int count = 0;
            foreach (var kvp in SpawnDefinitions)
            {
                var zoneName = kvp.Key;
                foreach (var def in kvp.Value)
                {
                    var pos = new Vec3(
                        def.Position.Length > 0 ? def.Position[0] : 0,
                        def.Position.Length > 1 ? def.Position[1] : 0,
                        def.Position.Length > 2 ? def.Position[2] : 0
                    );
                    var rot = new Quat(
                        def.Rotation.Length > 0 ? def.Rotation[0] : 0,
                        def.Rotation.Length > 1 ? def.Rotation[1] : 0,
                        def.Rotation.Length > 2 ? def.Rotation[2] : 0,
                        def.Rotation.Length > 3 ? def.Rotation[3] : 1
                    );

                    var entityType = ParseEntityType(def.EntityType);
                    var maxHP = GetNpcMaxHP(def.NpcId, def.MaxHP);

                    spawnManager.RegisterSpawnPoint(
                        zoneName, def.SpawnerId, def.NpcId,
                        pos, rot, entityType, def.IsRare, maxHP
                    );
                    count++;
                }
            }

            ServerLogger.Info($"Registered {count} spawn points with SpawnManager.", "SPAWN");
        }

        /// <summary>
        /// Gets the NPC definition for a given NPC ID, or null if not found.
        /// </summary>
        public NpcDefinition GetNpcDef(string npcId)
        {
            NpcDefinitions.TryGetValue(npcId, out var def);
            return def;
        }

        private int GetNpcMaxHP(string npcId, int fallback)
        {
            if (NpcDefinitions.TryGetValue(npcId, out var def) && def.MaxHP > 0)
                return def.MaxHP;
            return fallback > 0 ? fallback : 100;
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

        private void CreateTemplateNpcFile(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var template = new NpcDefinitionFile
                {
                    Npcs = new List<NpcDefinition>
                    {
                        new NpcDefinition
                        {
                            NpcId = "example_rat",
                            DisplayName = "Forest Rat",
                            Level = 1,
                            MaxHP = 50,
                            BaseDamage = 5,
                            BaseAC = 10,
                            AggroRange = 10f,
                            IsAggressive = true,
                        }
                    }
                };

                var json = JsonConvert.SerializeObject(template, Formatting.Indented);
                File.WriteAllText(path, json);
                ServerLogger.Info($"Created template NPC definitions at {path}", "SPAWN");
            }
            catch (Exception ex)
            {
                ServerLogger.Debug($"Could not create template NPC file: {ex.Message}", "SPAWN");
            }
        }

        private void CreateTemplateSpawnFile(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var template = new SpawnDefinitionFile
                {
                    Zones = new List<ZoneSpawnDefinition>
                    {
                        new ZoneSpawnDefinition
                        {
                            ZoneName = "StoneGrasslands",
                            Spawns = new List<SpawnDefinition>
                            {
                                new SpawnDefinition
                                {
                                    SpawnerId = 1,
                                    NpcId = "example_rat",
                                    Position = new float[] { 100f, 0f, 200f },
                                    Rotation = new float[] { 0f, 0f, 0f, 1f },
                                    RespawnTimeSeconds = 120f,
                                    MaxHP = 50,
                                    EntityType = "ENEMY"
                                }
                            }
                        }
                    }
                };

                var json = JsonConvert.SerializeObject(template, Formatting.Indented);
                File.WriteAllText(path, json);
                ServerLogger.Info($"Created template spawn definitions at {path}", "SPAWN");
            }
            catch (Exception ex)
            {
                ServerLogger.Debug($"Could not create template spawn file: {ex.Message}", "SPAWN");
            }
        }
    }

    // ====================================================
    // JSON DATA STRUCTURES
    // ====================================================

    public class NpcDefinitionFile
    {
        [JsonProperty("npcs")]
        public List<NpcDefinition> Npcs { get; set; } = new();
    }

    public class NpcDefinition
    {
        [JsonProperty("npc_id")]
        public string NpcId { get; set; } = "";

        [JsonProperty("display_name")]
        public string DisplayName { get; set; } = "";

        [JsonProperty("level")]
        public int Level { get; set; } = 1;

        [JsonProperty("max_hp")]
        public int MaxHP { get; set; } = 100;

        [JsonProperty("max_mp")]
        public int MaxMP { get; set; }

        [JsonProperty("base_damage")]
        public int BaseDamage { get; set; } = 10;

        [JsonProperty("base_ac")]
        public int BaseAC { get; set; } = 10;

        [JsonProperty("base_mr")]
        public int BaseMR { get; set; }

        [JsonProperty("base_pr")]
        public int BasePR { get; set; }

        [JsonProperty("base_vr")]
        public int BaseVR { get; set; }

        [JsonProperty("base_er")]
        public int BaseER { get; set; }

        [JsonProperty("attack_delay")]
        public float AttackDelay { get; set; } = 2.0f;

        [JsonProperty("aggro_range")]
        public float AggroRange { get; set; } = 15f;

        [JsonProperty("is_aggressive")]
        public bool IsAggressive { get; set; } = true;

        [JsonProperty("is_rare")]
        public bool IsRare { get; set; }

        [JsonProperty("loot_table")]
        public string LootTable { get; set; } = "";
    }

    public class SpawnDefinitionFile
    {
        [JsonProperty("zones")]
        public List<ZoneSpawnDefinition> Zones { get; set; } = new();
    }

    public class ZoneSpawnDefinition
    {
        [JsonProperty("zone_name")]
        public string ZoneName { get; set; } = "";

        [JsonProperty("spawns")]
        public List<SpawnDefinition> Spawns { get; set; } = new();
    }

    public class SpawnDefinition
    {
        [JsonProperty("spawner_id")]
        public int SpawnerId { get; set; }

        [JsonProperty("npc_id")]
        public string NpcId { get; set; } = "";

        [JsonProperty("position")]
        public float[] Position { get; set; } = new float[3];

        [JsonProperty("rotation")]
        public float[] Rotation { get; set; } = new float[] { 0, 0, 0, 1 };

        [JsonProperty("respawn_time_seconds")]
        public float RespawnTimeSeconds { get; set; } = 120f;

        [JsonProperty("max_hp")]
        public int MaxHP { get; set; }

        [JsonProperty("is_rare")]
        public bool IsRare { get; set; }

        [JsonProperty("wander_range")]
        public float WanderRange { get; set; } = 10f;

        [JsonProperty("entity_type")]
        public string EntityType { get; set; } = "ENEMY";
    }
}

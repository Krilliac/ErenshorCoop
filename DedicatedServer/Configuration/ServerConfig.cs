using System;
using System.Collections.Generic;
using System.IO;
using ErenshorDedicatedServer.Core;
using Newtonsoft.Json;

namespace ErenshorDedicatedServer.Configuration
{
    public class ServerConfig
    {
        private const string ConfigFileName = "server_config.json";

        // Server Identity
        [JsonProperty("server_name")]
        public string ServerName { get; set; } = "Erenshor Dedicated Server";

        [JsonProperty("motd")]
        public string MessageOfTheDay { get; set; } = "Welcome to the server!";

        // Network
        [JsonProperty("port")]
        public int Port { get; set; } = 7777;

        [JsonProperty("max_players")]
        public int MaxPlayers { get; set; } = 200;

        [JsonProperty("tick_rate")]
        public int TickRate { get; set; } = 30;

        [JsonProperty("connection_timeout_seconds")]
        public int ConnectionTimeoutSeconds { get; set; } = 30;

        [JsonProperty("max_channels")]
        public int MaxChannels { get; set; } = 14;

        // Gameplay
        [JsonProperty("pvp_enabled")]
        public bool PvpEnabled { get; set; }

        [JsonProperty("xp_modifier")]
        public float XpModifier { get; set; } = 1.0f;

        [JsonProperty("damage_modifier")]
        public float DamageModifier { get; set; } = 1.0f;

        [JsonProperty("hp_modifier")]
        public float HpModifier { get; set; } = 1.0f;

        [JsonProperty("loot_rate_modifier")]
        public float LootRateModifier { get; set; } = 1.0f;

        [JsonProperty("enable_zone_transfership")]
        public bool EnableZoneTransfership { get; set; } = true;

        [JsonProperty("entity_sync_distance")]
        public float EntitySyncDistance { get; set; } = 40f;

        [JsonProperty("enable_entity_sync_distance")]
        public bool EnableEntitySyncDistance { get; set; }

        // AI Settings
        [JsonProperty("npc_respawn_time_seconds")]
        public float NpcRespawnTimeSeconds { get; set; } = 300f;

        [JsonProperty("npc_aggro_range")]
        public float NpcAggroRange { get; set; } = 15f;

        [JsonProperty("npc_leash_range")]
        public float NpcLeashRange { get; set; } = 60f;

        [JsonProperty("npc_wander_range")]
        public float NpcWanderRange { get; set; } = 10f;

        [JsonProperty("npc_max_per_zone")]
        public int NpcMaxPerZone { get; set; } = 100;

        // Security
        [JsonProperty("ban_list")]
        public List<ulong> BanList { get; set; } = new List<ulong>();

        [JsonProperty("moderator_list")]
        public List<ulong> ModeratorList { get; set; } = new List<ulong>();

        [JsonProperty("admin_list")]
        public List<ulong> AdminList { get; set; } = new List<ulong>();

        [JsonProperty("whitelist_enabled")]
        public bool WhitelistEnabled { get; set; }

        [JsonProperty("whitelist")]
        public List<ulong> Whitelist { get; set; } = new List<ulong>();

        // Rate Limiting
        [JsonProperty("max_packets_per_second")]
        public int MaxPacketsPerSecond { get; set; } = 120;

        [JsonProperty("max_chat_messages_per_second")]
        public int MaxChatMessagesPerSecond { get; set; } = 5;

        [JsonProperty("max_movement_speed")]
        public float MaxMovementSpeed { get; set; } = 20f;

        // Logging
        [JsonProperty("log_level_console")]
        public string LogLevelConsole { get; set; } = "Info";

        [JsonProperty("log_level_file")]
        public string LogLevelFile { get; set; } = "Debug";

        [JsonProperty("log_directory")]
        public string LogDirectory { get; set; } = "logs";

        [JsonProperty("max_log_file_size_mb")]
        public int MaxLogFileSizeMb { get; set; } = 10;

        [JsonProperty("max_log_files")]
        public int MaxLogFiles { get; set; } = 10;

        // World Data
        [JsonProperty("zones")]
        public List<ZoneConfig> Zones { get; set; } = new List<ZoneConfig>();

        [JsonProperty("spawn_definitions_path")]
        public string SpawnDefinitionsPath { get; set; } = "data/spawns.json";

        [JsonProperty("npc_definitions_path")]
        public string NpcDefinitionsPath { get; set; } = "data/npcs.json";

        // Auto-save
        [JsonProperty("auto_save_interval_seconds")]
        public int AutoSaveIntervalSeconds { get; set; } = 300;

        public static ServerConfig Load(string path = null)
        {
            path ??= ConfigFileName;

            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var config = JsonConvert.DeserializeObject<ServerConfig>(json);

                    if (config == null)
                    {
                        ServerLogger.Error("Config file was empty or malformed. Using defaults.");
                        config = new ServerConfig();
                    }

                    config.Validate();
                    config.Save(path);
                    return config;
                }

                ServerLogger.Info("No config file found. Creating default configuration...");
                var defaultConfig = CreateDefault();
                defaultConfig.Save(path);
                return defaultConfig;
            }
            catch (JsonException ex)
            {
                ServerLogger.Error($"JSON parse error in config: {ex.Message}");
                ServerLogger.Info("Using default configuration.");
                var defaultConfig = CreateDefault();
                return defaultConfig;
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Failed to load config: {ex.Message}");
                return CreateDefault();
            }
        }

        public void Save(string path = null)
        {
            path ??= ConfigFileName;

            try
            {
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Failed to save config: {ex.Message}");
            }
        }

        public void Validate()
        {
            Port = Math.Clamp(Port, 1024, 65535);
            MaxPlayers = Math.Clamp(MaxPlayers, 1, 1000);
            TickRate = Math.Clamp(TickRate, 10, 120);
            ConnectionTimeoutSeconds = Math.Clamp(ConnectionTimeoutSeconds, 5, 120);
            MaxChannels = Math.Clamp(MaxChannels, 4, 64);
            XpModifier = Math.Clamp(XpModifier, 0.01f, 100f);
            DamageModifier = Math.Clamp(DamageModifier, 0.01f, 100f);
            HpModifier = Math.Clamp(HpModifier, 0.01f, 100f);
            LootRateModifier = Math.Clamp(LootRateModifier, 0.01f, 100f);
            EntitySyncDistance = Math.Clamp(EntitySyncDistance, 5f, 200f);
            NpcRespawnTimeSeconds = Math.Clamp(NpcRespawnTimeSeconds, 1f, 86400f);
            NpcAggroRange = Math.Clamp(NpcAggroRange, 1f, 100f);
            NpcLeashRange = Math.Clamp(NpcLeashRange, 10f, 500f);
            NpcWanderRange = Math.Clamp(NpcWanderRange, 0f, 100f);
            NpcMaxPerZone = Math.Clamp(NpcMaxPerZone, 1, 10000);
            MaxPacketsPerSecond = Math.Clamp(MaxPacketsPerSecond, 10, 1000);
            MaxChatMessagesPerSecond = Math.Clamp(MaxChatMessagesPerSecond, 1, 30);
            MaxMovementSpeed = Math.Clamp(MaxMovementSpeed, 5f, 100f);
            MaxLogFileSizeMb = Math.Clamp(MaxLogFileSizeMb, 1, 1000);
            MaxLogFiles = Math.Clamp(MaxLogFiles, 1, 100);
            AutoSaveIntervalSeconds = Math.Clamp(AutoSaveIntervalSeconds, 30, 86400);

            if (string.IsNullOrWhiteSpace(ServerName))
                ServerName = "Erenshor Dedicated Server";

            if (string.IsNullOrWhiteSpace(LogDirectory))
                LogDirectory = "logs";

            BanList ??= new List<ulong>();
            ModeratorList ??= new List<ulong>();
            AdminList ??= new List<ulong>();
            Whitelist ??= new List<ulong>();
            Zones ??= new List<ZoneConfig>();
        }

        private static ServerConfig CreateDefault()
        {
            var config = new ServerConfig();
            config.Zones = ZoneConfig.GetDefaultZones();
            config.Validate();
            return config;
        }

        // Runtime modification methods
        public bool AddBan(ulong steamId)
        {
            if (BanList.Contains(steamId)) return false;
            BanList.Add(steamId);
            Save();
            return true;
        }

        public bool RemoveBan(ulong steamId)
        {
            var removed = BanList.Remove(steamId);
            if (removed) Save();
            return removed;
        }

        public bool AddModerator(ulong steamId)
        {
            if (ModeratorList.Contains(steamId)) return false;
            ModeratorList.Add(steamId);
            Save();
            return true;
        }

        public bool RemoveModerator(ulong steamId)
        {
            var removed = ModeratorList.Remove(steamId);
            if (removed) Save();
            return removed;
        }

        public bool IsBanned(ulong steamId) => BanList.Contains(steamId);
        public bool IsModerator(ulong steamId) => ModeratorList.Contains(steamId);
        public bool IsAdmin(ulong steamId) => AdminList.Contains(steamId);
        public bool IsWhitelisted(ulong steamId) => !WhitelistEnabled || Whitelist.Contains(steamId);
    }

    public class ZoneConfig
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("display_name")]
        public string DisplayName { get; set; }

        [JsonProperty("max_npcs")]
        public int MaxNpcs { get; set; } = 50;

        [JsonProperty("spawn_points")]
        public List<SpawnPointConfig> SpawnPoints { get; set; } = new List<SpawnPointConfig>();

        public static List<ZoneConfig> GetDefaultZones()
        {
            return new List<ZoneConfig>
            {
                new ZoneConfig { Name = "StoneGrasslands", DisplayName = "Stone Grasslands", MaxNpcs = 50 },
                new ZoneConfig { Name = "StoneGrotto", DisplayName = "Stone Grotto", MaxNpcs = 30 },
                new ZoneConfig { Name = "DrownedForest", DisplayName = "Drowned Forest", MaxNpcs = 50 },
                new ZoneConfig { Name = "LonelyReach", DisplayName = "Lonely Reach", MaxNpcs = 40 },
                new ZoneConfig { Name = "Woodsmire", DisplayName = "Woodsmire", MaxNpcs = 40 },
                new ZoneConfig { Name = "InnAtTheEdge", DisplayName = "Inn at the Edge", MaxNpcs = 10 },
                new ZoneConfig { Name = "RatKingDomain", DisplayName = "Rat King Domain", MaxNpcs = 30 },
                new ZoneConfig { Name = "FernallaPortal", DisplayName = "Fernalla Portal", MaxNpcs = 20 },
                new ZoneConfig { Name = "TheMire", DisplayName = "The Mire", MaxNpcs = 50 },
                new ZoneConfig { Name = "SewerDungeon", DisplayName = "Sewer Dungeon", MaxNpcs = 40 },
                new ZoneConfig { Name = "Barrowdeep", DisplayName = "Barrowdeep", MaxNpcs = 50 },
                new ZoneConfig { Name = "ValeOfShadows", DisplayName = "Vale of Shadows", MaxNpcs = 50 },
                new ZoneConfig { Name = "StormBreaker", DisplayName = "Storm Breaker", MaxNpcs = 30 },
                new ZoneConfig { Name = "Glimmerhold", DisplayName = "Glimmerhold", MaxNpcs = 30 },
            };
        }
    }

    public class SpawnPointConfig
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("npc_id")]
        public string NpcId { get; set; }

        [JsonProperty("position")]
        public float[] Position { get; set; } = new float[3];

        [JsonProperty("rotation")]
        public float[] Rotation { get; set; } = new float[4];

        [JsonProperty("respawn_time")]
        public float RespawnTime { get; set; } = 300f;

        [JsonProperty("is_rare")]
        public bool IsRare { get; set; }

        [JsonProperty("wander_range")]
        public float WanderRange { get; set; } = 10f;
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;
using ErenshorDedicatedServer.Network;
using Newtonsoft.Json;

namespace ErenshorDedicatedServer.Persistence
{
    /// <summary>
    /// Manages player character persistence. Each character is saved as a JSON file
    /// keyed by SteamID. Saves player stats, position, gear, and progress.
    ///
    /// Save triggers:
    /// - Periodic auto-save (configurable interval)
    /// - Player disconnect
    /// - Server shutdown
    /// - Zone change
    ///
    /// Load triggers:
    /// - Player connect (after authentication)
    /// </summary>
    public class PlayerPersistence
    {
        private readonly string _saveDirectory;
        private readonly ConcurrentDictionary<ulong, DateTime> _lastSaveTimes = new();

        // Minimum time between saves for the same player (debounce)
        private const float MinSaveIntervalSeconds = 10f;

        public PlayerPersistence(string saveDirectory = "data/players")
        {
            _saveDirectory = saveDirectory;

            if (!Directory.Exists(_saveDirectory))
            {
                Directory.CreateDirectory(_saveDirectory);
                ServerLogger.Info($"Created player save directory: {_saveDirectory}", "PERSIST");
            }
        }

        /// <summary>
        /// Saves a player's current state to disk.
        /// </summary>
        public bool SavePlayer(PlayerSession session)
        {
            if (session == null || session.SteamId == 0) return false;

            // Debounce: skip if saved very recently
            if (_lastSaveTimes.TryGetValue(session.SteamId, out var lastSave))
            {
                if ((DateTime.UtcNow - lastSave).TotalSeconds < MinSaveIntervalSeconds)
                    return true; // Not an error, just skipped
            }

            try
            {
                var data = new PlayerSaveData
                {
                    SteamId = session.SteamId,
                    CharacterName = session.CharacterName,
                    Class = session.Class.ToString(),
                    Level = session.Level,
                    Health = session.Health,
                    MaxHealth = session.MaxHealth,
                    Mana = session.Mana,
                    MaxMana = session.MaxMana,
                    IsAlive = session.IsAlive,
                    Zone = session.Zone,
                    PositionX = session.Position.X,
                    PositionY = session.Position.Y,
                    PositionZ = session.Position.Z,
                    RotationX = session.Rotation.X,
                    RotationY = session.Rotation.Y,
                    RotationZ = session.Rotation.Z,
                    RotationW = session.Rotation.W,
                    IsMale = session.IsMale,
                    HairName = session.HairName,
                    HairColorR = session.HairColorR,
                    HairColorG = session.HairColorG,
                    HairColorB = session.HairColorB,
                    SkinColorR = session.SkinColorR,
                    SkinColorG = session.SkinColorG,
                    SkinColorB = session.SkinColorB,
                    LastSaved = DateTime.UtcNow,
                    TotalPlayTimeSeconds = (DateTime.UtcNow - session.ConnectedAt).TotalSeconds,
                };

                // Save gear
                if (session.Gear != null)
                {
                    data.Gear = session.Gear.Select(g => new GearSaveData
                    {
                        SlotType = g.SlotType,
                        ItemId = g.ItemId,
                        Quality = g.Quality
                    }).ToList();
                }

                // Save stats
                if (session.Stats != null)
                {
                    data.Stats = new StatsSaveData
                    {
                        Strength = session.Stats.Strength,
                        Dexterity = session.Stats.Dexterity,
                        Intelligence = session.Stats.Intelligence,
                        Wisdom = session.Stats.Wisdom,
                        Agility = session.Stats.Agility,
                        Endurance = session.Stats.Endurance,
                        Charisma = session.Stats.Charisma
                    };
                }

                var filePath = GetSavePath(session.SteamId);
                var json = JsonConvert.SerializeObject(data, Formatting.Indented);

                // Write to temp file first, then rename (atomic on most filesystems)
                var tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);

                _lastSaveTimes[session.SteamId] = DateTime.UtcNow;
                ServerLogger.Debug($"Saved player [{session.PlayerId}] {session.CharacterName} (Steam: {session.SteamId})", "PERSIST");
                return true;
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Failed to save player {session.CharacterName} (Steam: {session.SteamId}): {ex.Message}", "PERSIST");
                return false;
            }
        }

        /// <summary>
        /// Loads a player's saved state from disk. Returns null if no save exists.
        /// </summary>
        public PlayerSaveData LoadPlayer(ulong steamId)
        {
            if (steamId == 0) return null;

            var filePath = GetSavePath(steamId);
            if (!File.Exists(filePath)) return null;

            try
            {
                var json = File.ReadAllText(filePath);
                var data = JsonConvert.DeserializeObject<PlayerSaveData>(json);

                if (data != null)
                {
                    ServerLogger.Debug($"Loaded save data for Steam: {steamId} ({data.CharacterName})", "PERSIST");
                }

                return data;
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Failed to load player save for Steam {steamId}: {ex.Message}", "PERSIST");
                return null;
            }
        }

        /// <summary>
        /// Applies loaded save data to a player session.
        /// Only restores server-tracked data; clients handle their own rendering/inventory.
        /// </summary>
        public void ApplyToSession(PlayerSession session, PlayerSaveData data)
        {
            if (session == null || data == null) return;

            // Restore position (if player is in the same zone)
            // Don't override zone - client controls zone transitions
            if (!string.IsNullOrEmpty(data.Zone) && data.Zone == session.Zone)
            {
                session.Position = new Vec3(data.PositionX, data.PositionY, data.PositionZ);
                session.Rotation = new Quat(data.RotationX, data.RotationY, data.RotationZ, data.RotationW);
            }

            ServerLogger.Debug($"Applied save data to [{session.PlayerId}] {session.CharacterName}", "PERSIST");
        }

        /// <summary>
        /// Saves all currently connected players.
        /// </summary>
        public int SaveAllPlayers(IEnumerable<PlayerSession> sessions)
        {
            int saved = 0;
            foreach (var session in sessions)
            {
                if (session.IsAuthenticated && session.HasSentConnect)
                {
                    if (SavePlayer(session))
                        saved++;
                }
            }
            return saved;
        }

        /// <summary>
        /// Checks if a player has existing save data.
        /// </summary>
        public bool HasSaveData(ulong steamId)
        {
            return File.Exists(GetSavePath(steamId));
        }

        /// <summary>
        /// Deletes a player's save data.
        /// </summary>
        public bool DeleteSaveData(ulong steamId)
        {
            var path = GetSavePath(steamId);
            if (!File.Exists(path)) return false;

            try
            {
                File.Delete(path);
                ServerLogger.Info($"Deleted save data for Steam: {steamId}", "PERSIST");
                return true;
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Failed to delete save for Steam {steamId}: {ex.Message}", "PERSIST");
                return false;
            }
        }

        /// <summary>
        /// Gets all saved player Steam IDs.
        /// </summary>
        public List<ulong> GetAllSavedPlayers()
        {
            var result = new List<ulong>();
            try
            {
                foreach (var file in Directory.GetFiles(_saveDirectory, "*.json"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (ulong.TryParse(name, out var steamId))
                        result.Add(steamId);
                }
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Failed to enumerate player saves: {ex.Message}", "PERSIST");
            }
            return result;
        }

        private string GetSavePath(ulong steamId)
        {
            return Path.Combine(_saveDirectory, $"{steamId}.json");
        }
    }

    // ====================================================
    // SAVE DATA STRUCTURES
    // ====================================================

    public class PlayerSaveData
    {
        [JsonProperty("steam_id")]
        public ulong SteamId { get; set; }

        [JsonProperty("character_name")]
        public string CharacterName { get; set; } = "";

        [JsonProperty("class")]
        public string Class { get; set; } = "Unknown";

        [JsonProperty("level")]
        public int Level { get; set; } = 1;

        [JsonProperty("health")]
        public int Health { get; set; } = 100;

        [JsonProperty("max_health")]
        public int MaxHealth { get; set; } = 100;

        [JsonProperty("mana")]
        public int Mana { get; set; } = 100;

        [JsonProperty("max_mana")]
        public int MaxMana { get; set; } = 100;

        [JsonProperty("is_alive")]
        public bool IsAlive { get; set; } = true;

        [JsonProperty("zone")]
        public string Zone { get; set; } = "";

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

        // Look data
        [JsonProperty("is_male")]
        public bool IsMale { get; set; }

        [JsonProperty("hair_name")]
        public string HairName { get; set; } = "";

        [JsonProperty("hair_color_r")]
        public float HairColorR { get; set; }

        [JsonProperty("hair_color_g")]
        public float HairColorG { get; set; }

        [JsonProperty("hair_color_b")]
        public float HairColorB { get; set; }

        [JsonProperty("skin_color_r")]
        public float SkinColorR { get; set; }

        [JsonProperty("skin_color_g")]
        public float SkinColorG { get; set; }

        [JsonProperty("skin_color_b")]
        public float SkinColorB { get; set; }

        // Gear
        [JsonProperty("gear")]
        public List<GearSaveData> Gear { get; set; } = new();

        // Stats
        [JsonProperty("stats")]
        public StatsSaveData Stats { get; set; } = new();

        // Metadata
        [JsonProperty("last_saved")]
        public DateTime LastSaved { get; set; }

        [JsonProperty("total_play_time_seconds")]
        public double TotalPlayTimeSeconds { get; set; }
    }

    public class GearSaveData
    {
        [JsonProperty("slot")]
        public byte SlotType { get; set; }

        [JsonProperty("item_id")]
        public string ItemId { get; set; } = "";

        [JsonProperty("quality")]
        public byte Quality { get; set; }
    }

    public class StatsSaveData
    {
        [JsonProperty("str")]
        public int Strength { get; set; }

        [JsonProperty("dex")]
        public int Dexterity { get; set; }

        [JsonProperty("int")]
        public int Intelligence { get; set; }

        [JsonProperty("wis")]
        public int Wisdom { get; set; }

        [JsonProperty("agi")]
        public int Agility { get; set; }

        [JsonProperty("end")]
        public int Endurance { get; set; }

        [JsonProperty("cha")]
        public int Charisma { get; set; }
    }
}

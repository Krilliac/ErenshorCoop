using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ErenshorDedicatedServer.Configuration;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;
using Newtonsoft.Json;

namespace ErenshorDedicatedServer.Persistence
{
    /// <summary>
    /// Automatically records game data reported by clients and writes it to the
    /// server's persistence files. This allows the server to build its spawn/NPC
    /// database from actual client gameplay data when no pre-existing data files exist.
    ///
    /// Two modes:
    /// 1. auto_record_client_data: Continuously captures new spawns/NPCs as clients
    ///    report them. Merges with existing data (never overwrites).
    /// 2. full_data_grab_on_first_connect: On first player connect, marks the server
    ///    as being in "data grab" mode. While active, all zones a player visits
    ///    are fully recorded. Once all known zones are recorded (or the admin marks
    ///    it complete), recording stops and the server becomes self-sufficient.
    /// </summary>
    public class DataRecorder
    {
        private readonly ServerConfig _config;
        private readonly SpawnDefinitionLoader _spawnLoader;

        // In-memory buffers of recorded data (zone -> list of recorded spawns)
        private readonly ConcurrentDictionary<string, List<RecordedSpawn>> _recordedSpawns = new();
        private readonly ConcurrentDictionary<string, RecordedNpc> _recordedNpcs = new();
        private readonly ConcurrentDictionary<string, RecordedItem> _recordedItems = new();

        // Track what we've flushed to disk to avoid duplicates
        private readonly ConcurrentDictionary<string, HashSet<int>> _flushedSpawnerIds = new();
        private readonly HashSet<string> _flushedItemIds = new();

        private bool _isRecording;
        private bool _isFullGrab;
        private DateTime _lastFlush;
        private int _totalRecordedSpawns;
        private int _totalRecordedNpcs;
        private int _totalRecordedItems;
        private int _zonesRecorded;

        // Flush buffer to disk every N seconds
        private const float FlushIntervalSeconds = 30f;

        public bool IsRecording => _isRecording;
        public bool IsFullGrab => _isFullGrab;
        public int TotalRecordedSpawns => _totalRecordedSpawns;
        public int TotalRecordedNpcs => _totalRecordedNpcs;
        public int TotalRecordedItems => _totalRecordedItems;
        public int ZonesRecorded => _zonesRecorded;

        public DataRecorder(ServerConfig config, SpawnDefinitionLoader spawnLoader)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _spawnLoader = spawnLoader ?? throw new ArgumentNullException(nameof(spawnLoader));
            _lastFlush = DateTime.UtcNow;
        }

        /// <summary>
        /// Initializes the recorder based on config and current data state.
        /// </summary>
        public void Initialize()
        {
            // If recording is marked complete, don't record
            if (_config.DataRecordingComplete)
            {
                _isRecording = false;
                _isFullGrab = false;
                ServerLogger.Info("Data recording marked complete. Recorder inactive.", "RECORD");
                return;
            }

            // If auto-record is enabled and we don't have loaded data, start recording
            if (_config.AutoRecordClientData)
            {
                _isRecording = true;
                ServerLogger.Info("Auto-record enabled. Client-reported data will be captured.", "RECORD");
            }

            // If full data grab is enabled and we haven't recorded all zones yet
            if (_config.FullDataGrabOnFirstConnect && !_spawnLoader.HasLoadedData)
            {
                _isFullGrab = true;
                _isRecording = true;
                ServerLogger.Info("Full data grab mode active. Will record all zone data from first connected players.", "RECORD");
            }

            // Load existing flushed spawner IDs to prevent duplicates
            LoadExistingSpawnerIds();
        }

        /// <summary>
        /// Records a spawn point reported by a client. Called from PacketRouter
        /// when an ENTITY_SPAWN packet is processed.
        /// </summary>
        public void RecordSpawn(string zone, int spawnerId, string npcId,
            Vec3 position, Quat rotation, EntityType entityType, bool isRare, int maxHP,
            bool syncStats = false, int level = 0, int baseAC = 0, int baseHP = 0,
            int baseMR = 0, int basePR = 0, int baseVR = 0, int baseER = 0,
            int baseDMG = 0, float atkDelay = 0f)
        {
            if (!_isRecording) return;
            if (string.IsNullOrWhiteSpace(zone) || string.IsNullOrWhiteSpace(npcId)) return;

            // Skip SIMs and PETs - they're player-bound companions, not world spawns
            if (entityType == EntityType.SIM || entityType == EntityType.PET) return;

            // Skip if this spawner was already flushed to disk
            if (_flushedSpawnerIds.TryGetValue(zone, out var flushed) && flushed.Contains(spawnerId))
                return;

            // Skip if already in the SpawnDefinitionLoader's loaded data
            if (_spawnLoader.SpawnDefinitions.TryGetValue(zone, out var existing))
            {
                if (existing.Any(s => s.SpawnerId == spawnerId))
                    return;
            }

            // Record the spawn
            var recorded = new RecordedSpawn
            {
                SpawnerId = spawnerId,
                NpcId = npcId,
                Position = new float[] { position.X, position.Y, position.Z },
                Rotation = new float[] { rotation.X, rotation.Y, rotation.Z, rotation.W },
                EntityType = entityType.ToString(),
                IsRare = isRare,
                MaxHP = maxHP,
                RecordedAt = DateTime.UtcNow
            };

            var zoneList = _recordedSpawns.GetOrAdd(zone, _ => new List<RecordedSpawn>());
            lock (zoneList)
            {
                // Don't add duplicate spawner IDs
                if (zoneList.Any(s => s.SpawnerId == spawnerId))
                    return;

                zoneList.Add(recorded);
                _totalRecordedSpawns++;
            }

            // Record the NPC definition if we have stats
            if (!_recordedNpcs.ContainsKey(npcId))
            {
                var npcDef = new RecordedNpc
                {
                    NpcId = npcId,
                    MaxHP = syncStats ? baseHP : maxHP,
                    Level = level,
                    BaseAC = baseAC,
                    BaseMR = baseMR,
                    BasePR = basePR,
                    BaseVR = baseVR,
                    BaseER = baseER,
                    BaseDMG = baseDMG,
                    AttackDelay = atkDelay,
                    IsRare = isRare,
                    EntityType = entityType.ToString(),
                    HasSyncedStats = syncStats
                };

                if (_recordedNpcs.TryAdd(npcId, npcDef))
                    _totalRecordedNpcs++;
            }

            ServerLogger.Debug($"Recorded spawn: {npcId} (spawner {spawnerId}) in {zone}", "RECORD");
        }

        /// <summary>
        /// Records an item seen in the world (from gear or drops).
        /// Builds a registry of known item IDs and their observed properties.
        /// Full item stats (damage, armor, effects) require client decompilation.
        /// </summary>
        public void RecordItem(string itemId, byte slotType = 0, byte quality = 0, string sourceZone = "")
        {
            if (!_isRecording) return;
            if (string.IsNullOrWhiteSpace(itemId)) return;
            if (_flushedItemIds.Contains(itemId)) return;

            if (_recordedItems.TryGetValue(itemId, out var existing))
            {
                // Update with richer data if available
                if (slotType != 0 && existing.SlotType == 0) existing.SlotType = slotType;
                if (quality != 0 && existing.Quality == 0) existing.Quality = quality;
                if (!string.IsNullOrEmpty(sourceZone) && !existing.SeenInZones.Contains(sourceZone))
                    existing.SeenInZones.Add(sourceZone);
                return;
            }

            var item = new RecordedItem
            {
                ItemId = itemId,
                SlotType = slotType,
                Quality = quality,
                RecordedAt = DateTime.UtcNow
            };
            if (!string.IsNullOrEmpty(sourceZone))
                item.SeenInZones.Add(sourceZone);

            if (_recordedItems.TryAdd(itemId, item))
                _totalRecordedItems++;
        }

        /// <summary>
        /// Records all gear items from a player's equipment.
        /// Called when gear data is received in PLAYER_DATA packets.
        /// </summary>
        public void RecordGearItems(IEnumerable<(byte slotType, string itemId, byte quality)> gearItems)
        {
            if (!_isRecording) return;

            foreach (var (slotType, itemId, quality) in gearItems)
            {
                RecordItem(itemId, slotType, quality);
            }
        }

        /// <summary>
        /// Records an item from a world drop.
        /// </summary>
        public void RecordDroppedItem(string itemId, string zone, int quality)
        {
            if (!_isRecording) return;
            RecordItem(itemId, sourceZone: zone, quality: (byte)Math.Clamp(quality, 0, 255));
        }

        /// <summary>
        /// Called when a zone has had all its entities reported by the zone owner.
        /// Marks the zone as "recorded" for full data grab tracking.
        /// </summary>
        public void MarkZoneRecorded(string zone)
        {
            if (!_isRecording || string.IsNullOrWhiteSpace(zone)) return;

            if (!_config.RecordedZones.Contains(zone))
            {
                _config.RecordedZones.Add(zone);
                _zonesRecorded = _config.RecordedZones.Count;
                ServerLogger.Info($"Zone '{zone}' data recorded. ({_zonesRecorded}/{_config.Zones.Count} zones)", "RECORD");

                // Check if all known zones have been recorded
                if (_isFullGrab && AllZonesRecorded())
                {
                    CompleteRecording();
                }
            }
        }

        /// <summary>
        /// Called every tick. Periodically flushes buffered data to disk.
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (!_isRecording) return;

            if ((DateTime.UtcNow - _lastFlush).TotalSeconds >= FlushIntervalSeconds)
            {
                _lastFlush = DateTime.UtcNow;
                FlushToDisk();
            }
        }

        /// <summary>
        /// Forces an immediate flush of all buffered data to disk.
        /// </summary>
        public void FlushToDisk()
        {
            if (_totalRecordedSpawns == 0 && _totalRecordedNpcs == 0 && _totalRecordedItems == 0)
                return;

            try
            {
                FlushSpawnDefinitions();
                FlushNpcDefinitions();
                FlushItemDefinitions();
                ServerLogger.Debug($"Flushed recorded data: {_totalRecordedSpawns} spawns, {_totalRecordedNpcs} NPCs, {_totalRecordedItems} items", "RECORD");
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Failed to flush recorded data: {ex.Message}", "RECORD");
            }
        }

        /// <summary>
        /// Marks recording as complete. Flushes remaining data and saves config.
        /// After this, recording stops permanently until the admin resets it.
        /// </summary>
        public void CompleteRecording()
        {
            FlushToDisk();

            _isRecording = false;
            _isFullGrab = false;
            _config.DataRecordingComplete = true;
            _config.Save();

            ServerLogger.Info($"Data recording complete! Recorded {_totalRecordedSpawns} spawns across " +
                $"{_config.RecordedZones.Count} zones, {_totalRecordedNpcs} NPC definitions.", "RECORD");
            ServerLogger.Info("Server will now use recorded data for spawns. Set data_recording_complete=false to re-record.", "RECORD");
        }

        /// <summary>
        /// Resets recording state so data can be re-recorded.
        /// Does NOT delete existing data files - it will merge with them.
        /// </summary>
        public void ResetRecording()
        {
            _config.DataRecordingComplete = false;
            _config.RecordedZones.Clear();
            _isRecording = _config.AutoRecordClientData || _config.FullDataGrabOnFirstConnect;
            _isFullGrab = _config.FullDataGrabOnFirstConnect;
            _config.Save();

            ServerLogger.Info("Data recording reset. Will begin recording on next client connection.", "RECORD");
        }

        private bool AllZonesRecorded()
        {
            if (_config.Zones == null || _config.Zones.Count == 0)
                return false;

            foreach (var zone in _config.Zones)
            {
                if (!_config.RecordedZones.Contains(zone.Name))
                    return false;
            }
            return true;
        }

        private void LoadExistingSpawnerIds()
        {
            // Load existing spawn definitions to track what's already on disk
            if (_spawnLoader.SpawnDefinitions == null) return;

            foreach (var kvp in _spawnLoader.SpawnDefinitions)
            {
                var ids = new HashSet<int>();
                foreach (var spawn in kvp.Value)
                    ids.Add(spawn.SpawnerId);
                _flushedSpawnerIds[kvp.Key] = ids;
            }
        }

        private void FlushSpawnDefinitions()
        {
            var path = _config.SpawnDefinitionsPath;
            if (string.IsNullOrWhiteSpace(path)) return;

            // Load existing file
            SpawnDefinitionFile file = null;
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    file = JsonConvert.DeserializeObject<SpawnDefinitionFile>(json);
                }
                catch { /* Start fresh if corrupt */ }
            }

            file ??= new SpawnDefinitionFile();
            file.Zones ??= new List<ZoneSpawnDefinition>();

            // Merge recorded spawns into the file
            foreach (var kvp in _recordedSpawns)
            {
                var zoneName = kvp.Key;
                List<RecordedSpawn> spawns;
                lock (kvp.Value)
                {
                    spawns = kvp.Value.ToList();
                }

                // Find or create the zone entry
                var zoneDef = file.Zones.FirstOrDefault(z => z.ZoneName == zoneName);
                if (zoneDef == null)
                {
                    zoneDef = new ZoneSpawnDefinition { ZoneName = zoneName, Spawns = new List<SpawnDefinition>() };
                    file.Zones.Add(zoneDef);
                }

                zoneDef.Spawns ??= new List<SpawnDefinition>();
                var flushedIds = _flushedSpawnerIds.GetOrAdd(zoneName, _ => new HashSet<int>());

                foreach (var recorded in spawns)
                {
                    // Skip if already in the file
                    if (zoneDef.Spawns.Any(s => s.SpawnerId == recorded.SpawnerId))
                    {
                        flushedIds.Add(recorded.SpawnerId);
                        continue;
                    }

                    zoneDef.Spawns.Add(new SpawnDefinition
                    {
                        SpawnerId = recorded.SpawnerId,
                        NpcId = recorded.NpcId,
                        Position = recorded.Position,
                        Rotation = recorded.Rotation,
                        RespawnTimeSeconds = _config.NpcRespawnTimeSeconds,
                        MaxHP = recorded.MaxHP,
                        IsRare = recorded.IsRare,
                        EntityType = recorded.EntityType
                    });

                    flushedIds.Add(recorded.SpawnerId);
                }
            }

            // Write atomically
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tempPath = path + ".tmp";
            var output = JsonConvert.SerializeObject(file, Formatting.Indented);
            File.WriteAllText(tempPath, output);
            File.Move(tempPath, path, overwrite: true);

            // Also update the SpawnDefinitionLoader's in-memory state
            foreach (var zoneDef in file.Zones)
            {
                _spawnLoader.SpawnDefinitions[zoneDef.ZoneName] = zoneDef.Spawns;
            }
        }

        private void FlushNpcDefinitions()
        {
            var path = _config.NpcDefinitionsPath;
            if (string.IsNullOrWhiteSpace(path)) return;

            // Load existing file
            NpcDefinitionFile file = null;
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    file = JsonConvert.DeserializeObject<NpcDefinitionFile>(json);
                }
                catch { /* Start fresh if corrupt */ }
            }

            file ??= new NpcDefinitionFile();
            file.Npcs ??= new List<NpcDefinition>();

            // Merge recorded NPCs
            foreach (var kvp in _recordedNpcs)
            {
                var recorded = kvp.Value;

                // Skip if already exists
                if (file.Npcs.Any(n => n.NpcId == recorded.NpcId))
                    continue;

                file.Npcs.Add(new NpcDefinition
                {
                    NpcId = recorded.NpcId,
                    DisplayName = recorded.NpcId, // Best we can do from client data
                    Level = recorded.Level,
                    MaxHP = recorded.MaxHP,
                    BaseDamage = recorded.BaseDMG,
                    BaseAC = recorded.BaseAC,
                    BaseMR = recorded.BaseMR,
                    BasePR = recorded.BasePR,
                    BaseVR = recorded.BaseVR,
                    BaseER = recorded.BaseER,
                    AttackDelay = recorded.AttackDelay > 0 ? recorded.AttackDelay : 2.0f,
                    IsRare = recorded.IsRare,
                });

                // Also add to in-memory loader
                _spawnLoader.NpcDefinitions[recorded.NpcId] = file.Npcs.Last();
            }

            // Write atomically
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tempPath = path + ".tmp";
            var output = JsonConvert.SerializeObject(file, Formatting.Indented);
            File.WriteAllText(tempPath, output);
            File.Move(tempPath, path, overwrite: true);
        }

        private void FlushItemDefinitions()
        {
            if (_recordedItems.IsEmpty) return;

            var path = "data/items.json";

            // Load existing file
            ItemDefinitionFile file = null;
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    file = JsonConvert.DeserializeObject<ItemDefinitionFile>(json);
                }
                catch { /* Start fresh if corrupt */ }
            }

            file ??= new ItemDefinitionFile();
            file.Items ??= new List<ItemDefinition>();

            // Merge recorded items
            foreach (var kvp in _recordedItems)
            {
                var recorded = kvp.Value;

                if (file.Items.Any(i => i.ItemId == recorded.ItemId))
                {
                    _flushedItemIds.Add(recorded.ItemId);
                    continue;
                }

                file.Items.Add(new ItemDefinition
                {
                    ItemId = recorded.ItemId,
                    SlotType = recorded.SlotType,
                    Quality = recorded.Quality,
                    SeenInZones = recorded.SeenInZones.ToList(),
                    FirstRecorded = recorded.RecordedAt
                });

                _flushedItemIds.Add(recorded.ItemId);
            }

            // Write atomically
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tempPath = path + ".tmp";
            var output = JsonConvert.SerializeObject(file, Formatting.Indented);
            File.WriteAllText(tempPath, output);
            File.Move(tempPath, path, overwrite: true);
        }
    }

    // ====================================================
    // INTERNAL RECORDING STRUCTURES
    // ====================================================

    internal class RecordedSpawn
    {
        public int SpawnerId { get; set; }
        public string NpcId { get; set; }
        public float[] Position { get; set; }
        public float[] Rotation { get; set; }
        public string EntityType { get; set; }
        public bool IsRare { get; set; }
        public int MaxHP { get; set; }
        public DateTime RecordedAt { get; set; }
    }

    internal class RecordedNpc
    {
        public string NpcId { get; set; }
        public int MaxHP { get; set; }
        public int Level { get; set; }
        public int BaseAC { get; set; }
        public int BaseMR { get; set; }
        public int BasePR { get; set; }
        public int BaseVR { get; set; }
        public int BaseER { get; set; }
        public int BaseDMG { get; set; }
        public float AttackDelay { get; set; }
        public bool IsRare { get; set; }
        public string EntityType { get; set; }
        public bool HasSyncedStats { get; set; }
    }

    internal class RecordedItem
    {
        public string ItemId { get; set; }
        public byte SlotType { get; set; }
        public byte Quality { get; set; }
        public List<string> SeenInZones { get; set; } = new();
        public DateTime RecordedAt { get; set; }
    }

    // ====================================================
    // ITEM PERSISTENCE STRUCTURES
    // ====================================================

    public class ItemDefinitionFile
    {
        [JsonProperty("items")]
        public List<ItemDefinition> Items { get; set; } = new();
    }

    public class ItemDefinition
    {
        [JsonProperty("item_id")]
        public string ItemId { get; set; } = "";

        [JsonProperty("slot_type")]
        public byte SlotType { get; set; }

        [JsonProperty("quality")]
        public byte Quality { get; set; }

        /// <summary>
        /// Zones where this item has been observed (gear or drops).
        /// Useful for identifying zone-specific loot tables.
        /// </summary>
        [JsonProperty("seen_in_zones")]
        public List<string> SeenInZones { get; set; } = new();

        [JsonProperty("first_recorded")]
        public DateTime FirstRecorded { get; set; }

        // Placeholder fields for post-decompilation enrichment
        [JsonProperty("display_name")]
        public string DisplayName { get; set; } = "";

        [JsonProperty("description")]
        public string Description { get; set; } = "";

        [JsonProperty("item_type")]
        public string ItemType { get; set; } = "";

        [JsonProperty("stats")]
        public Dictionary<string, int> Stats { get; set; } = new();
    }
}

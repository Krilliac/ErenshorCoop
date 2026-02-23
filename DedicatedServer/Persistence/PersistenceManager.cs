using System;
using ErenshorDedicatedServer.Configuration;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Network;
using ErenshorDedicatedServer.World;

namespace ErenshorDedicatedServer.Persistence
{
    /// <summary>
    /// Central persistence coordinator. Manages auto-save scheduling, startup loading,
    /// shutdown saving, and data recording. Owns all persistence components and provides
    /// a unified interface for ServerCore.
    /// </summary>
    public class PersistenceManager
    {
        public SpawnDefinitionLoader SpawnLoader { get; }
        public PlayerPersistence PlayerPersistence { get; }
        public WorldStatePersistence WorldPersistence { get; }
        public DataRecorder DataRecorder { get; }

        private readonly ServerConfig _config;
        private DateTime _lastAutoSave;
        private int _autoSaveCount;

        public PersistenceManager(ServerConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            SpawnLoader = new SpawnDefinitionLoader(config);
            PlayerPersistence = new PlayerPersistence("data/players");
            WorldPersistence = new WorldStatePersistence("data/world_state.json");
            DataRecorder = new DataRecorder(config, SpawnLoader);

            _lastAutoSave = DateTime.UtcNow;
        }

        /// <summary>
        /// Loads all persistent data on server startup.
        /// Call after all subsystems are initialized but before accepting connections.
        /// </summary>
        public void LoadAll(WorldManager worldManager, SpawnManager spawnManager)
        {
            ServerLogger.Info("Loading persistent data...", "PERSIST");

            // 1. Load NPC and spawn definitions
            SpawnLoader.LoadAll();
            SpawnLoader.RegisterSpawnsWithManager(spawnManager);

            // 2. Restore world state if available
            var worldState = WorldPersistence.LoadWorldState();
            if (worldState != null)
            {
                WorldPersistence.RestoreWorldState(worldState, worldManager);
            }

            // 3. Initialize data recorder (after loading, so it knows what's already on disk)
            DataRecorder.Initialize();

            ServerLogger.Info("Persistent data loading complete.", "PERSIST");
        }

        /// <summary>
        /// Saves all persistent data on server shutdown.
        /// </summary>
        public void SaveAll(WorldManager worldManager, SpawnManager spawnManager, NetworkManager network)
        {
            ServerLogger.Info("Saving all persistent data...", "PERSIST");

            // Flush any remaining recorded data
            DataRecorder.FlushToDisk();

            // Save all connected players
            var sessions = network.GetAllSessions();
            var playerCount = PlayerPersistence.SaveAllPlayers(sessions);

            // Save world state
            WorldPersistence.SaveWorldState(worldManager, spawnManager);

            ServerLogger.Info($"Saved {playerCount} players and world state.", "PERSIST");
        }

        /// <summary>
        /// Called every tick to check if auto-save should run and to tick the data recorder.
        /// </summary>
        public void Tick(float deltaTime, WorldManager worldManager, SpawnManager spawnManager, NetworkManager network)
        {
            // Tick data recorder (handles its own flush interval)
            DataRecorder.Tick(deltaTime);

            if (_config.AutoSaveIntervalSeconds <= 0) return;

            if ((DateTime.UtcNow - _lastAutoSave).TotalSeconds >= _config.AutoSaveIntervalSeconds)
            {
                _lastAutoSave = DateTime.UtcNow;
                PerformAutoSave(worldManager, spawnManager, network);
            }
        }

        /// <summary>
        /// Performs an auto-save of all game state.
        /// </summary>
        private void PerformAutoSave(WorldManager worldManager, SpawnManager spawnManager, NetworkManager network)
        {
            _autoSaveCount++;

            try
            {
                var sessions = network.GetAllSessions();
                var playerCount = PlayerPersistence.SaveAllPlayers(sessions);
                WorldPersistence.SaveWorldState(worldManager, spawnManager);

                ServerLogger.Info($"Auto-save #{_autoSaveCount}: {playerCount} players, world state saved.", "PERSIST");
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Auto-save #{_autoSaveCount} failed: {ex.Message}", "PERSIST");
            }
        }

        /// <summary>
        /// Saves a single player (on disconnect, zone change, etc.).
        /// </summary>
        public void SavePlayer(PlayerSession session)
        {
            PlayerPersistence.SavePlayer(session);
        }

        /// <summary>
        /// Loads player data on connect and applies it.
        /// </summary>
        public void OnPlayerAuthenticated(PlayerSession session)
        {
            var saveData = PlayerPersistence.LoadPlayer(session.SteamId);
            if (saveData != null)
            {
                PlayerPersistence.ApplyToSession(session, saveData);
                ServerLogger.Debug($"Restored save data for {session.CharacterName} (Steam: {session.SteamId})", "PERSIST");
            }
        }

        public int GetAutoSaveCount() => _autoSaveCount;
    }
}

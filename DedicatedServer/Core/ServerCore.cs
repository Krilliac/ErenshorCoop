using System;
using System.Collections.Generic;
using System.Linq;
using ErenshorDedicatedServer.Chat;
using ErenshorDedicatedServer.Configuration;
using ErenshorDedicatedServer.Data;
using ErenshorDedicatedServer.Network;
using ErenshorDedicatedServer.Persistence;
using ErenshorDedicatedServer.World;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ErenshorDedicatedServer.Core
{
    /// <summary>
    /// The central server orchestrator. Owns all subsystems, wires them together,
    /// and drives the main update loop. This is the authoritative game server.
    /// </summary>
    public class ServerCore
    {
        public ServerConfig Config { get; }
        public NetworkManager Network { get; private set; }
        public WorldManager WorldManager { get; private set; }
        public ChatManager ChatManager { get; private set; }
        public GroupManager GroupManager { get; private set; }
        public ItemDropManager ItemDropManager { get; private set; }
        public WeatherManager WeatherManager { get; private set; }
        public PacketRouter PacketRouter { get; private set; }

        // New subsystems
        public ThreatManager ThreatManager { get; private set; }
        public CreatureAI CreatureAI { get; private set; }
        public SpawnManager SpawnManager { get; private set; }
        public SpatialGrid SpatialGrid { get; private set; }
        public CombatManager CombatManager { get; private set; }
        public CooldownManager CooldownManager { get; private set; }
        public EventScheduler EventScheduler { get; private set; }
        public MovementGenerator MovementGenerator { get; private set; }
        public UpdateFieldTracker UpdateFieldTracker { get; private set; }
        public SessionStateMachine SessionStateMachine { get; private set; }
        public PersistenceManager Persistence { get; private set; }

        // Server state
        public DateTime StartTime { get; private set; }
        public bool IsRunning { get; private set; }

        // Performance tracking
        private float _tickAccumulator;
        private int _tickCount;
        private float _avgTickTime;
        private DateTime _lastPlayerListBroadcast;
        private DateTime _lastStatLog;

        public ServerCore(ServerConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public bool Start()
        {
            try
            {
                // Initialize core subsystems
                Network = new NetworkManager(Config);
                WorldManager = new WorldManager(Config, Network);
                ChatManager = new ChatManager(Network, Config);
                GroupManager = new GroupManager(Network);
                ItemDropManager = new ItemDropManager(Network);
                WeatherManager = new WeatherManager(Network);

                // Initialize new subsystems
                ThreatManager = new ThreatManager();
                SpatialGrid = new SpatialGrid(Config.SpatialGridCellSize);
                CreatureAI = new CreatureAI(WorldManager, ThreatManager);
                SpawnManager = new SpawnManager(WorldManager, Network);
                CombatManager = new CombatManager(WorldManager, ThreatManager, SpatialGrid);
                CooldownManager = new CooldownManager();
                EventScheduler = new EventScheduler();
                MovementGenerator = new MovementGenerator(WorldManager);
                UpdateFieldTracker = new UpdateFieldTracker();
                SessionStateMachine = new SessionStateMachine();
                Persistence = new PersistenceManager(Config);

                // Initialize packet router (needs reference to this for all subsystems)
                PacketRouter = new PacketRouter(Network, this);

                // Wire events
                Network.OnPacketReceived += PacketRouter.HandlePacket;
                Network.OnPlayerConnected += OnPlayerConnected;
                Network.OnPlayerDisconnected += OnPlayerDisconnected;

                // Load persistent data before accepting connections
                Persistence.LoadAll(WorldManager, SpawnManager);

                // Start networking
                if (!Network.Start())
                    return false;

                StartTime = DateTime.UtcNow;
                IsRunning = true;
                _lastPlayerListBroadcast = DateTime.UtcNow;
                _lastStatLog = DateTime.UtcNow;

                ServerLogger.Info("All subsystems initialized.", "CORE");
                return true;
            }
            catch (Exception ex)
            {
                ServerLogger.Fatal($"Server start failed: {ex.Message}");
                ServerLogger.Fatal($"Stack trace: {ex.StackTrace}");
                return false;
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;

            try
            {
                // Notify all players
                ChatManager?.BroadcastInfoMessage("[Server] Server is shutting down...");

                // Save all persistent data
                Persistence?.SaveAll(WorldManager, SpawnManager, Network);

                // Save config
                Config?.Save();

                // Stop networking
                Network?.Stop();
                Network?.Dispose();

                ServerLogger.Info("Server stopped.", "CORE");
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Error during stop: {ex.Message}", "CORE");
            }
        }

        /// <summary>
        /// Main server tick. Called from the game loop at the configured tick rate.
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (!IsRunning) return;

            var tickStart = DateTime.UtcNow;

            try
            {
                // Poll network events
                Network.PollEvents();

                // Update world state
                WorldManager.Tick(deltaTime);

                // Update new subsystems
                ThreatManager.Tick(deltaTime);
                CreatureAI.Tick(deltaTime);
                SpawnManager.Tick(deltaTime);
                CooldownManager.Tick(deltaTime);
                EventScheduler.Tick(deltaTime);

                // Update item drops
                ItemDropManager.Tick(deltaTime);

                // Auto-save
                Persistence.Tick(deltaTime, WorldManager, SpawnManager, Network);

                // Periodic player list broadcast (every 10 seconds)
                if ((DateTime.UtcNow - _lastPlayerListBroadcast).TotalSeconds >= 10)
                {
                    _lastPlayerListBroadcast = DateTime.UtcNow;
                    if (Network.ConnectedPlayerCount > 0)
                        Network.SendPlayerList();
                }

                // Periodic stat logging (every 60 seconds)
                if ((DateTime.UtcNow - _lastStatLog).TotalSeconds >= 60)
                {
                    _lastStatLog = DateTime.UtcNow;
                    LogPeriodicStats();
                }

                // Check for timed out players
                CheckTimeouts();
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Tick error: {ex.Message}", "CORE");
                ServerLogger.Debug($"Stack trace: {ex.StackTrace}", "CORE");
            }

            // Track tick performance
            var tickDuration = (float)(DateTime.UtcNow - tickStart).TotalMilliseconds;
            _tickAccumulator += tickDuration;
            _tickCount++;

            if (_tickCount >= Config.TickRate) // Average over 1 second worth of ticks
            {
                _avgTickTime = _tickAccumulator / _tickCount;
                _tickAccumulator = 0;
                _tickCount = 0;

                if (_avgTickTime > 1000f / Config.TickRate)
                {
                    ServerLogger.Warning($"Server falling behind! Avg tick: {_avgTickTime:F2}ms (budget: {1000f / Config.TickRate:F2}ms)", "PERF");
                }
            }
        }

        // ====================================================
        // CONNECTION EVENTS
        // ====================================================

        private void OnPlayerConnected(PlayerSession session)
        {
            ServerLogger.Info($"Player connected: [{session.PlayerId}] from {session.Peer.Address}", "CORE");

            // Initialize session state machine
            SessionStateMachine.InitSession(session.PlayerId);
            SessionStateMachine.TryTransition(session.PlayerId, ConnectionPhase.Handshaking);

            // Register for update tracking
            UpdateFieldTracker.RegisterReceiver(session.PlayerId);
        }

        private void OnPlayerDisconnected(PlayerSession session, DisconnectInfo info)
        {
            if (session == null) return;

            try
            {
                ServerLogger.Info($"Player disconnected: [{session.PlayerId}] {session.CharacterName} ({info.Reason})", "CORE");

                // Clean up group
                GroupManager.OnPlayerDisconnect(session.PlayerId);

                // Clean up world
                WorldManager.OnPlayerDisconnect(session);

                // Save player data before cleanup
                Persistence?.SavePlayer(session);

                // Clean up new subsystems
                ThreatManager.RemoveSource(session.PlayerId);
                CooldownManager.ResetCooldowns(session.PlayerId);
                MovementGenerator.RemoveEntity(session.PlayerId);
                SpatialGrid.Remove(session.PlayerId);
                UpdateFieldTracker.UnregisterReceiver(session.PlayerId);
                SessionStateMachine.RemoveSession(session.PlayerId);
                EventScheduler.CancelByTag($"player_{session.PlayerId}");

                // Notify other players
                Network.BroadcastPlayerDisconnect(session.PlayerId);

                // Update player list
                if (Network.ConnectedPlayerCount > 0)
                    Network.SendPlayerList();
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Error handling disconnect for [{session.PlayerId}]: {ex.Message}", "CORE");
            }
        }

        // ====================================================
        // RELAY EXISTING STATE TO NEW PLAYER
        // ====================================================

        /// <summary>
        /// Sends all existing player connection data to a newly connected player.
        /// </summary>
        public void SendExistingPlayersTo(PlayerSession newPlayer)
        {
            if (newPlayer == null) return;

            foreach (var kvp in Network.Players)
            {
                var existing = kvp.Value;
                if (existing.PlayerId == newPlayer.PlayerId) continue;
                if (!existing.HasSentConnect) continue;

                try
                {
                    // Build a connection packet for the existing player
                    var writer = new NetDataWriter();
                    writer.Put((byte)PacketType.PLAYER_CONNECT);
                    writer.Put(existing.PlayerId);
                    writer.Put(false); // not sim

                    var dataTypes = new HashSet<PlayerDataType>
                    {
                        PlayerDataType.NAME,
                        PlayerDataType.LEVEL,
                        PlayerDataType.CLASS,
                        PlayerDataType.SCENE
                    };
                    writer.Put(PacketHelper.GetSubTypeFlag(dataTypes));

                    // NAME data
                    writer.Put(existing.CharacterName);
                    writer.Put(existing.IsMale);
                    writer.Put(existing.HairName);
                    writer.Put(existing.HairColorR);
                    writer.Put(existing.HairColorG);
                    writer.Put(existing.HairColorB);
                    writer.Put(existing.SkinColorR);
                    writer.Put(existing.SkinColorG);
                    writer.Put(existing.SkinColorB);
                    writer.Put(existing.SteamId);

                    // LEVEL
                    writer.Put(existing.Level);

                    // CLASS
                    writer.Put((byte)existing.Class);

                    // SCENE
                    writer.Put(existing.Zone);

                    Network.SendTo(newPlayer, writer, DeliveryMethod.ReliableOrdered,
                        PacketHelper.GetChannel(PacketType.PLAYER_CONNECT));
                }
                catch (Exception ex)
                {
                    ServerLogger.Error($"Error sending existing player [{existing.PlayerId}] to [{newPlayer.PlayerId}]: {ex.Message}", "CORE");
                }
            }
        }

        // ====================================================
        // MOD COMMANDS
        // ====================================================

        /// <summary>
        /// Handles moderator commands (kick/ban) from players.
        /// </summary>
        public void HandleModCommand(PlayerSession session, int commandType, string playerName)
        {
            if (session == null) return;

            // Permission check
            if (!session.IsModerator && !session.IsAdmin)
            {
                ChatManager.SendInfoMessage(session, "[Server] Insufficient permission.");
                return;
            }

            playerName = PacketHelper.Sanitize(playerName, 64).ToLowerInvariant();

            var target = Network.GetAllSessions()
                .FirstOrDefault(s => s.CharacterName.ToLowerInvariant() == playerName && s.PlayerId != session.PlayerId);

            if (target == null)
            {
                ChatManager.SendInfoMessage(session, "[Server] Player not found.");
                return;
            }

            // Can't kick/ban other mods unless admin
            if ((target.IsModerator || target.IsAdmin) && !session.IsAdmin)
            {
                ChatManager.SendInfoMessage(session, "[Server] Insufficient permission.");
                return;
            }

            switch (commandType)
            {
                case 0: // Kick
                    var kickMsg = $"[Server] Player {target.CharacterName} has been kicked by {session.CharacterName}.";
                    ChatManager.BroadcastInfoMessage(kickMsg);
                    Network.DisconnectPlayer(target, $"Kicked by {session.CharacterName}");
                    ServerLogger.Info($"[{session.PlayerId}] {session.CharacterName} kicked [{target.PlayerId}] {target.CharacterName}", "MOD");
                    break;

                case 1: // Ban
                    if (Config.AddBan(target.SteamId))
                    {
                        var banMsg = $"[Server] Player {target.CharacterName} has been banned by {session.CharacterName}.";
                        ChatManager.BroadcastInfoMessage(banMsg);
                        Network.DisconnectPlayer(target, $"Banned by {session.CharacterName}");
                        ServerLogger.Info($"[{session.PlayerId}] {session.CharacterName} banned [{target.PlayerId}] {target.CharacterName} (Steam: {target.SteamId})", "MOD");
                    }
                    else
                    {
                        ChatManager.SendInfoMessage(session, "[Server] Player is already banned.");
                    }
                    break;

                default:
                    ChatManager.SendInfoMessage(session, "[Server] Unknown command.");
                    break;
            }
        }

        // ====================================================
        // DIAGNOSTICS
        // ====================================================

        private void CheckTimeouts()
        {
            foreach (var session in Network.GetAllSessions())
            {
                if (session.HasTimedOut(Config.ConnectionTimeoutSeconds * 2))
                {
                    ServerLogger.Warning($"Player [{session.PlayerId}] {session.CharacterName} timed out", "CORE");
                    Network.DisconnectPlayer(session, "Timeout");
                }
            }
        }

        private void LogPeriodicStats()
        {
            if (Network.ConnectedPlayerCount == 0) return;

            ServerLogger.Info($"Players: {Network.ConnectedPlayerCount}/{Config.MaxPlayers} | " +
                           $"Entities: {WorldManager.GetTotalEntityCount()} | " +
                           $"Groups: {GroupManager.GetGroupCount()} | " +
                           $"Drops: {ItemDropManager.GetTotalDropCount()} | " +
                           $"Spawns: {SpawnManager.GetSpawnPointCount()} (pending: {SpawnManager.GetPendingRespawnCount()}) | " +
                           $"Threat: {ThreatManager.GetTableCount()} | " +
                           $"Grid: {SpatialGrid.GetEntityCount()}/{SpatialGrid.GetCellCount()} | " +
                           $"Events: {EventScheduler.GetPendingCount()} | " +
                           $"Avg Tick: {_avgTickTime:F2}ms | " +
                           $"Uptime: {GetUptimeString()}", "STATS");
        }

        public string GetUptimeString()
        {
            var uptime = DateTime.UtcNow - StartTime;
            if (uptime.TotalDays >= 1)
                return $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m";
            if (uptime.TotalHours >= 1)
                return $"{uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s";
            return $"{uptime.Minutes}m {uptime.Seconds}s";
        }

        public float GetAvgTickTime() => _avgTickTime;
    }
}

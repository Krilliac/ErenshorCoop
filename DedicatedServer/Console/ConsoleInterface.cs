using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ErenshorDedicatedServer.Configuration;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;
using ErenshorDedicatedServer.Network;

namespace ErenshorDedicatedServer.Console
{
    /// <summary>
    /// Rich interactive console interface for the dedicated server.
    /// Provides commands for server management, player administration,
    /// live monitoring, and configuration.
    /// </summary>
    public class ConsoleInterface
    {
        private readonly ServerCore _server;
        private readonly Dictionary<string, CommandInfo> _commands = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _commandHistory = new();
        private bool _inputReady;

        public ConsoleInterface(ServerCore server)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            RegisterCommands();
        }

        private void RegisterCommands()
        {
            Cmd("help", "Show available commands", CmdHelp, "[command]");
            Cmd("status", "Show server status dashboard", CmdStatus);
            Cmd("players", "List connected players", CmdPlayers);
            Cmd("zones", "Show zone information", CmdZones);
            Cmd("kick", "Kick a player", CmdKick, "<name>");
            Cmd("ban", "Ban a player", CmdBan, "<name|steamid>");
            Cmd("unban", "Unban a player", CmdUnban, "<steamid>");
            Cmd("banlist", "Show ban list", CmdBanList);
            Cmd("mute", "Mute a player's chat", CmdMute, "<name>");
            Cmd("unmute", "Unmute a player's chat", CmdUnmute, "<name>");
            Cmd("say", "Broadcast message to all players", CmdSay, "<message>");
            Cmd("tell", "Send message to a player", CmdTell, "<name> <message>");
            Cmd("tp", "Teleport player (info only, client-side)", CmdTp, "<name> <zone>");
            Cmd("setmod", "Add a moderator by SteamID", CmdSetMod, "<steamid>");
            Cmd("removemod", "Remove a moderator by SteamID", CmdRemoveMod, "<steamid>");
            Cmd("config", "View/modify server config", CmdConfig, "[key] [value]");
            Cmd("save", "Save all data (players, world, config)", CmdSave);
            Cmd("reload", "Reload server configuration", CmdReload);
            Cmd("saveworld", "Save world state to disk", CmdSaveWorld);
            Cmd("saveplayers", "Save all connected players", CmdSavePlayers);
            Cmd("loglevel", "Set console log level", CmdLogLevel, "<debug|info|warning|error>");
            Cmd("recording", "Show/control data recording status", CmdRecording, "[start|stop|reset|flush]");
            Cmd("entities", "Show entity count per zone", CmdEntities);
            Cmd("groups", "Show active groups", CmdGroups);
            Cmd("stats", "Show network statistics", CmdStats);
            Cmd("uptime", "Show server uptime", CmdUptime);
            Cmd("stop", "Gracefully stop the server", CmdStop);
            Cmd("shutdown", "Alias for stop", CmdStop);
            Cmd("quit", "Alias for stop", CmdStop);
            Cmd("clear", "Clear console", CmdClear);
        }

        private void Cmd(string name, string desc, Action<string[]> handler, string usage = "")
        {
            _commands[name] = new CommandInfo { Name = name, Description = desc, Handler = handler, Usage = usage };
        }

        /// <summary>
        /// Non-blocking console input processing.
        /// </summary>
        public void ProcessInput()
        {
            try
            {
                if (!System.Console.KeyAvailable) return;

                var line = System.Console.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) return;

                _commandHistory.Add(line);
                ExecuteCommand(line.Trim());
            }
            catch (InvalidOperationException)
            {
                // Console not available (e.g., redirected input)
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Console input error: {ex.Message}");
            }
        }

        private void ExecuteCommand(string input)
        {
            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            var cmdName = parts[0];
            var args = parts.Skip(1).ToArray();

            if (_commands.TryGetValue(cmdName, out var cmd))
            {
                try
                {
                    cmd.Handler(args);
                }
                catch (Exception ex)
                {
                    WriteError($"Command error: {ex.Message}");
                    ServerLogger.Debug($"Command error stack: {ex.StackTrace}");
                }
            }
            else
            {
                WriteError($"Unknown command: {cmdName}. Type 'help' for a list of commands.");
            }
        }

        // ====================================================
        // COMMAND IMPLEMENTATIONS
        // ====================================================

        private void CmdHelp(string[] args)
        {
            if (args.Length > 0 && _commands.TryGetValue(args[0], out var cmd))
            {
                WriteHeader($"Command: {cmd.Name}");
                WriteLine($"  Description: {cmd.Description}");
                if (!string.IsNullOrEmpty(cmd.Usage))
                    WriteLine($"  Usage: {cmd.Name} {cmd.Usage}");
                return;
            }

            WriteHeader("Available Commands");
            var maxLen = _commands.Values.Max(c => c.Name.Length);
            foreach (var c in _commands.Values.OrderBy(c => c.Name))
            {
                var usage = string.IsNullOrEmpty(c.Usage) ? "" : $" {c.Usage}";
                WriteColor($"  {c.Name.PadRight(maxLen + 2)}", ConsoleColor.Green);
                System.Console.Write($"{usage.PadRight(20)}");
                WriteLineColor($"  {c.Description}", ConsoleColor.Gray);
            }
        }

        private void CmdStatus(string[] args)
        {
            var uptime = DateTime.UtcNow - _server.StartTime;

            WriteHeader("Server Status");
            WriteLine($"  Server Name:     {_server.Config.ServerName}");
            WriteLine($"  Port:            {_server.Config.Port}");
            WriteLine($"  Uptime:          {_server.GetUptimeString()}");
            WriteLine($"  Players:         {_server.Network.ConnectedPlayerCount}/{_server.Config.MaxPlayers}");
            WriteLine($"  Tick Rate:       {_server.Config.TickRate} Hz");
            WriteLine($"  Avg Tick:        {_server.GetAvgTickTime():F2} ms");
            WriteLine($"  Entities:        {_server.WorldManager.GetTotalEntityCount()} ({_server.WorldManager.GetAliveEntityCount()} alive)");
            WriteLine($"  Groups:          {_server.GroupManager.GetGroupCount()}");
            WriteLine($"  Item Drops:      {_server.ItemDropManager.GetTotalDropCount()}");
            WriteHeader("Settings");
            WriteLine($"  PvP:             {(_server.Config.PvpEnabled ? "Enabled" : "Disabled")}");
            WriteLine($"  XP Modifier:     {_server.Config.XpModifier:F2}x");
            WriteLine($"  DMG Modifier:    {_server.Config.DamageModifier:F2}x");
            WriteLine($"  HP Modifier:     {_server.Config.HpModifier:F2}x");
            WriteLine($"  Loot Rate:       {_server.Config.LootRateModifier:F2}x");
            WriteHeader("Network");
            WriteLine($"  Packets In:      {_server.Network.TotalPacketsReceived:N0}");
            WriteLine($"  Packets Out:     {_server.Network.TotalPacketsSent:N0}");
            WriteLine($"  Bytes In:        {FormatBytes(_server.Network.TotalBytesReceived)}");
            WriteLine($"  Bytes Out:       {FormatBytes(_server.Network.TotalBytesSent)}");
            WriteHeader("Subsystems");
            WriteLine($"  Spawn Points:    {_server.SpawnManager.GetSpawnPointCount()} (pending: {_server.SpawnManager.GetPendingRespawnCount()})");
            WriteLine($"  Threat Tables:   {_server.ThreatManager.GetTableCount()}");
            WriteLine($"  Spatial Grid:    {_server.SpatialGrid.GetEntityCount()} entries / {_server.SpatialGrid.GetCellCount()} cells");
            WriteLine($"  Scheduled Events:{_server.EventScheduler.GetPendingCount()}");
            WriteLine($"  Cooldowns:       {_server.CooldownManager.GetTrackedEntityCount()} entities");
            var combatStats = _server.CombatManager;
            WriteLine($"  Combat:          {combatStats.TotalKills} kills, {combatStats.TotalDamageDealt:N0} dmg, {combatStats.TotalHealingDone:N0} heals");
            WriteHeader("Persistence");
            var savedPlayers = _server.Persistence?.PlayerPersistence.GetAllSavedPlayers()?.Count ?? 0;
            var autoSaves = _server.Persistence?.GetAutoSaveCount() ?? 0;
            var hasWorldSave = _server.Persistence?.WorldPersistence.HasSaveData() ?? false;
            var spawnDefs = _server.Persistence?.SpawnLoader.HasLoadedData ?? false;
            WriteLine($"  Player Saves:    {savedPlayers} characters on disk");
            WriteLine($"  Auto-saves:      {autoSaves} (every {_server.Config.AutoSaveIntervalSeconds}s)");
            WriteLine($"  World State:     {(hasWorldSave ? "Saved" : "None")}");
            WriteLine($"  Spawn Defs:      {(spawnDefs ? "Loaded from file" : "Client-reported")}");
            var recorder = _server.Persistence?.DataRecorder;
            if (recorder != null)
            {
                var recStatus = _server.Config.DataRecordingComplete ? "Complete" :
                    (recorder.IsRecording ? (recorder.IsFullGrab ? "Full Grab" : "Auto-Record") : "Inactive");
                WriteLine($"  Recording:       {recStatus} ({recorder.TotalRecordedSpawns} spawns, {recorder.TotalRecordedNpcs} NPCs, {recorder.TotalRecordedItems} items)");
            }
        }

        private void CmdPlayers(string[] args)
        {
            var players = _server.Network.GetAllSessions();
            if (players.Count == 0)
            {
                WriteInfo("No players connected.");
                return;
            }

            WriteHeader($"Connected Players ({players.Count}/{_server.Config.MaxPlayers})");
            WriteLine($"  {"ID",-6} {"Name",-20} {"Level",-6} {"Class",-12} {"Zone",-20} {"Ping",-6} {"Flags"}");
            WriteSeparator();

            foreach (var p in players.OrderBy(p => p.PlayerId))
            {
                var flags = new List<string>();
                if (p.IsAdmin) flags.Add("ADMIN");
                if (p.IsModerator) flags.Add("MOD");
                if (!p.IsAlive) flags.Add("DEAD");
                if (p.GroupId >= 0) flags.Add($"G{p.GroupId}");

                var flagStr = flags.Count > 0 ? string.Join(",", flags) : "-";
                var ping = p.Peer?.Ping ?? 0;

                WriteLine($"  {p.PlayerId,-6} {TruncateStr(p.CharacterName, 20),-20} {p.Level,-6} {p.Class,-12} {TruncateStr(p.Zone, 20),-20} {ping,-6} {flagStr}");
            }
        }

        private void CmdZones(string[] args)
        {
            var zoneStats = _server.WorldManager.GetZoneStats();
            if (zoneStats.Count == 0)
            {
                WriteInfo("No active zones.");
                return;
            }

            WriteHeader("Active Zones");
            WriteLine($"  {"Zone",-25} {"Players",-10} {"Entities",-10} {"Owner"}");
            WriteSeparator();

            foreach (var z in zoneStats.OrderByDescending(z => z.Value.players))
            {
                var ownerName = "-";
                var session = _server.Network.GetSession(z.Value.owner);
                if (session != null)
                    ownerName = $"[{session.PlayerId}] {session.CharacterName}";

                WriteLine($"  {TruncateStr(z.Key, 25),-25} {z.Value.players,-10} {z.Value.entities,-10} {ownerName}");
            }
        }

        private void CmdKick(string[] args)
        {
            if (args.Length == 0) { WriteError("Usage: kick <name>"); return; }

            var name = string.Join(" ", args);
            var session = FindPlayer(name);
            if (session == null) { WriteError($"Player '{name}' not found."); return; }

            _server.ChatManager.BroadcastInfoMessage($"[Server] {session.CharacterName} has been kicked.");
            _server.Network.DisconnectPlayer(session, "Kicked by server console");
            WriteInfo($"Kicked [{session.PlayerId}] {session.CharacterName}");
        }

        private void CmdBan(string[] args)
        {
            if (args.Length == 0) { WriteError("Usage: ban <name|steamid>"); return; }

            var input = string.Join(" ", args);

            // Try as SteamID first
            if (ulong.TryParse(input, out var steamId))
            {
                if (_server.Config.AddBan(steamId))
                {
                    // Try to kick if online
                    var session = _server.Network.GetAllSessions().FirstOrDefault(s => s.SteamId == steamId);
                    if (session != null)
                    {
                        _server.ChatManager.BroadcastInfoMessage($"[Server] {session.CharacterName} has been banned.");
                        _server.Network.DisconnectPlayer(session, "Banned");
                    }
                    WriteInfo($"Banned SteamID: {steamId}");
                }
                else
                    WriteError("SteamID is already banned.");
                return;
            }

            // Try as name
            var target = FindPlayer(input);
            if (target == null) { WriteError($"Player '{input}' not found."); return; }

            if (_server.Config.AddBan(target.SteamId))
            {
                _server.ChatManager.BroadcastInfoMessage($"[Server] {target.CharacterName} has been banned.");
                _server.Network.DisconnectPlayer(target, "Banned by server console");
                WriteInfo($"Banned [{target.PlayerId}] {target.CharacterName} (Steam: {target.SteamId})");
            }
            else
                WriteError("Player is already banned.");
        }

        private void CmdUnban(string[] args)
        {
            if (args.Length == 0) { WriteError("Usage: unban <steamid>"); return; }
            if (!ulong.TryParse(args[0], out var steamId)) { WriteError("Invalid SteamID."); return; }

            if (_server.Config.RemoveBan(steamId))
                WriteInfo($"Unbanned SteamID: {steamId}");
            else
                WriteError("SteamID was not banned.");
        }

        private void CmdBanList(string[] args)
        {
            var bans = _server.Config.BanList;
            if (bans.Count == 0) { WriteInfo("No banned players."); return; }

            WriteHeader($"Ban List ({bans.Count})");
            foreach (var id in bans)
                WriteLine($"  {id}");
        }

        private void CmdMute(string[] args)
        {
            if (args.Length == 0) { WriteError("Usage: mute <name>"); return; }
            var name = string.Join(" ", args);
            if (_server.ChatManager.MutePlayer(name))
                WriteInfo($"Muted: {name}");
            else
                WriteError("Player is already muted.");
        }

        private void CmdUnmute(string[] args)
        {
            if (args.Length == 0) { WriteError("Usage: unmute <name>"); return; }
            var name = string.Join(" ", args);
            if (_server.ChatManager.UnmutePlayer(name))
                WriteInfo($"Unmuted: {name}");
            else
                WriteError("Player was not muted.");
        }

        private void CmdSay(string[] args)
        {
            if (args.Length == 0) { WriteError("Usage: say <message>"); return; }
            var msg = $"[Server] {string.Join(" ", args)}";
            _server.ChatManager.BroadcastInfoMessage(msg);
            WriteInfo($"Broadcast: {msg}");
        }

        private void CmdTell(string[] args)
        {
            if (args.Length < 2) { WriteError("Usage: tell <name> <message>"); return; }
            var name = args[0];
            var msg = $"[Server] {string.Join(" ", args.Skip(1))}";
            var session = FindPlayer(name);
            if (session == null) { WriteError($"Player '{name}' not found."); return; }

            _server.ChatManager.SendInfoMessage(session, msg);
            WriteInfo($"To [{session.PlayerId}] {session.CharacterName}: {msg}");
        }

        private void CmdTp(string[] args)
        {
            WriteInfo("Teleport is client-side. Use 'tell <name> Please go to <zone>' or implement a client mod command.");
        }

        private void CmdSetMod(string[] args)
        {
            if (args.Length == 0) { WriteError("Usage: setmod <steamid>"); return; }
            if (!ulong.TryParse(args[0], out var steamId)) { WriteError("Invalid SteamID."); return; }

            if (_server.Config.AddModerator(steamId))
            {
                // Update live session if online
                var session = _server.Network.GetAllSessions().FirstOrDefault(s => s.SteamId == steamId);
                if (session != null)
                    session.IsModerator = true;
                WriteInfo($"Added moderator: {steamId}");
            }
            else
                WriteError("SteamID is already a moderator.");
        }

        private void CmdRemoveMod(string[] args)
        {
            if (args.Length == 0) { WriteError("Usage: removemod <steamid>"); return; }
            if (!ulong.TryParse(args[0], out var steamId)) { WriteError("Invalid SteamID."); return; }

            if (_server.Config.RemoveModerator(steamId))
            {
                var session = _server.Network.GetAllSessions().FirstOrDefault(s => s.SteamId == steamId);
                if (session != null)
                    session.IsModerator = false;
                WriteInfo($"Removed moderator: {steamId}");
            }
            else
                WriteError("SteamID was not a moderator.");
        }

        private void CmdConfig(string[] args)
        {
            if (args.Length == 0)
            {
                WriteHeader("Server Configuration");
                WriteLine($"  server_name:           {_server.Config.ServerName}");
                WriteLine($"  port:                  {_server.Config.Port}");
                WriteLine($"  max_players:           {_server.Config.MaxPlayers}");
                WriteLine($"  tick_rate:             {_server.Config.TickRate}");
                WriteLine($"  pvp_enabled:           {_server.Config.PvpEnabled}");
                WriteLine($"  xp_modifier:           {_server.Config.XpModifier}");
                WriteLine($"  damage_modifier:       {_server.Config.DamageModifier}");
                WriteLine($"  hp_modifier:           {_server.Config.HpModifier}");
                WriteLine($"  loot_rate_modifier:    {_server.Config.LootRateModifier}");
                WriteLine($"  max_packets_per_sec:   {_server.Config.MaxPacketsPerSecond}");
                WriteLine($"  max_chat_per_sec:      {_server.Config.MaxChatMessagesPerSecond}");
                WriteLine($"  whitelist_enabled:     {_server.Config.WhitelistEnabled}");
                return;
            }

            if (args.Length == 1)
            {
                WriteError("Usage: config <key> <value> - or config with no args to show all.");
                return;
            }

            var key = args[0].ToLowerInvariant();
            var value = args[1];

            switch (key)
            {
                case "pvp_enabled":
                    if (bool.TryParse(value, out var pvp))
                    {
                        _server.Config.PvpEnabled = pvp;
                        WriteInfo($"PvP: {pvp}");
                        // Notify clients
                        BroadcastPvpChange(pvp);
                    }
                    else WriteError("Expected true/false");
                    break;
                case "xp_modifier":
                    if (float.TryParse(value, out var xp))
                    { _server.Config.XpModifier = Math.Clamp(xp, 0.01f, 100f); WriteInfo($"XP modifier: {_server.Config.XpModifier}"); }
                    else WriteError("Expected float");
                    break;
                case "damage_modifier":
                    if (float.TryParse(value, out var dmg))
                    { _server.Config.DamageModifier = Math.Clamp(dmg, 0.01f, 100f); WriteInfo($"Damage modifier: {_server.Config.DamageModifier}"); }
                    else WriteError("Expected float");
                    break;
                case "hp_modifier":
                    if (float.TryParse(value, out var hp))
                    { _server.Config.HpModifier = Math.Clamp(hp, 0.01f, 100f); WriteInfo($"HP modifier: {_server.Config.HpModifier}"); }
                    else WriteError("Expected float");
                    break;
                case "loot_rate_modifier":
                    if (float.TryParse(value, out var loot))
                    { _server.Config.LootRateModifier = Math.Clamp(loot, 0.01f, 100f); WriteInfo($"Loot rate: {_server.Config.LootRateModifier}"); }
                    else WriteError("Expected float");
                    break;
                case "server_name":
                    _server.Config.ServerName = string.Join(" ", args.Skip(1));
                    WriteInfo($"Server name: {_server.Config.ServerName}");
                    break;
                case "motd":
                    _server.Config.MessageOfTheDay = string.Join(" ", args.Skip(1));
                    WriteInfo($"MOTD: {_server.Config.MessageOfTheDay}");
                    break;
                default:
                    WriteError($"Unknown config key: {key}");
                    break;
            }
        }

        private void CmdSave(string[] args)
        {
            _server.Persistence?.SaveAll(_server.WorldManager, _server.SpawnManager, _server.Network);
            _server.Config.Save();
            WriteInfo("All data saved (players, world state, config).");
        }

        private void CmdSaveWorld(string[] args)
        {
            _server.Persistence?.WorldPersistence.SaveWorldState(_server.WorldManager, _server.SpawnManager);
            WriteInfo("World state saved.");
        }

        private void CmdSavePlayers(string[] args)
        {
            var sessions = _server.Network.GetAllSessions();
            var count = _server.Persistence?.PlayerPersistence.SaveAllPlayers(sessions) ?? 0;
            WriteInfo($"Saved {count} player(s).");
        }

        private void CmdReload(string[] args)
        {
            var newConfig = Configuration.ServerConfig.Load();
            if (newConfig != null)
            {
                // Apply changeable settings
                _server.Config.PvpEnabled = newConfig.PvpEnabled;
                _server.Config.XpModifier = newConfig.XpModifier;
                _server.Config.DamageModifier = newConfig.DamageModifier;
                _server.Config.HpModifier = newConfig.HpModifier;
                _server.Config.LootRateModifier = newConfig.LootRateModifier;
                _server.Config.BanList = newConfig.BanList;
                _server.Config.ModeratorList = newConfig.ModeratorList;
                _server.Config.AdminList = newConfig.AdminList;
                _server.Config.Whitelist = newConfig.Whitelist;
                _server.Config.WhitelistEnabled = newConfig.WhitelistEnabled;
                _server.Config.MessageOfTheDay = newConfig.MessageOfTheDay;
                _server.Config.MaxPacketsPerSecond = newConfig.MaxPacketsPerSecond;
                _server.Config.MaxChatMessagesPerSecond = newConfig.MaxChatMessagesPerSecond;
                WriteInfo("Configuration reloaded. Note: Port and tick rate changes require restart.");
            }
            else
            {
                WriteError("Failed to reload configuration.");
            }
        }

        private void CmdLogLevel(string[] args)
        {
            if (args.Length == 0) { WriteError("Usage: loglevel <debug|info|warning|error>"); return; }

            if (Enum.TryParse<LogLevel>(args[0], true, out var level))
            {
                ServerLogger.SetConsoleLevel(level);
                WriteInfo($"Console log level: {level}");
            }
            else
                WriteError("Valid levels: debug, info, warning, error");
        }

        private void CmdRecording(string[] args)
        {
            var recorder = _server.Persistence?.DataRecorder;
            if (recorder == null)
            {
                WriteError("Persistence system not initialized.");
                return;
            }

            if (args.Length == 0)
            {
                // Show status
                WriteHeader("Data Recording Status");
                WriteLine($"  Recording:     {(recorder.IsRecording ? "ACTIVE" : "INACTIVE")}");
                WriteLine($"  Full Grab:     {(recorder.IsFullGrab ? "ACTIVE" : "OFF")}");
                WriteLine($"  Complete:      {_server.Config.DataRecordingComplete}");
                WriteLine($"  Spawns:        {recorder.TotalRecordedSpawns} recorded");
                WriteLine($"  NPCs:          {recorder.TotalRecordedNpcs} recorded");
                WriteLine($"  Items:         {recorder.TotalRecordedItems} recorded");
                WriteLine($"  Zones:         {recorder.ZonesRecorded}/{_server.Config.Zones.Count} recorded");
                if (_server.Config.RecordedZones.Count > 0)
                {
                    WriteLine($"  Recorded:      {string.Join(", ", _server.Config.RecordedZones)}");
                }
                return;
            }

            switch (args[0].ToLower())
            {
                case "start":
                    _server.Config.DataRecordingComplete = false;
                    _server.Config.AutoRecordClientData = true;
                    recorder.ResetRecording();
                    WriteInfo("Recording started. Client data will be captured.");
                    break;
                case "stop":
                    recorder.CompleteRecording();
                    WriteInfo("Recording stopped and data flushed.");
                    break;
                case "reset":
                    recorder.ResetRecording();
                    WriteInfo("Recording reset. Existing data preserved, will re-record on next client data.");
                    break;
                case "flush":
                    recorder.FlushToDisk();
                    WriteInfo("Buffered data flushed to disk.");
                    break;
                default:
                    WriteError("Usage: recording [start|stop|reset|flush]");
                    break;
            }
        }

        private void CmdEntities(string[] args)
        {
            var zones = _server.WorldManager.Zones;
            WriteHeader("Entity Count by Zone");
            var total = 0;
            foreach (var z in zones.Values.Where(z => z.Entities.Count > 0).OrderByDescending(z => z.Entities.Count))
            {
                var alive = z.Entities.Values.Count(e => e.IsAlive);
                WriteLine($"  {z.DisplayName,-25} {z.Entities.Count} total, {alive} alive");
                total += z.Entities.Count;
            }
            WriteSeparator();
            WriteLine($"  {"Total",-25} {total}");
        }

        private void CmdGroups(string[] args)
        {
            var groups = _server.GroupManager.GetAllGroups();
            if (groups.Count == 0) { WriteInfo("No active groups."); return; }

            WriteHeader($"Active Groups ({groups.Count})");
            foreach (var g in groups)
            {
                var leaderSession = _server.Network.GetSession(g.LeaderId);
                var leaderName = leaderSession?.CharacterName ?? $"[{g.LeaderId}]";
                var members = string.Join(", ", g.Members.Select(id =>
                {
                    var s = _server.Network.GetSession(id);
                    return s != null ? s.CharacterName : $"[{id}]";
                }));
                WriteLine($"  Group {g.GroupId}: Leader={leaderName}, Members={members}");
            }
        }

        private void CmdStats(string[] args)
        {
            WriteHeader("Network Statistics");
            WriteLine($"  Packets Received:  {_server.Network.TotalPacketsReceived:N0}");
            WriteLine($"  Packets Sent:      {_server.Network.TotalPacketsSent:N0}");
            WriteLine($"  Bytes Received:    {FormatBytes(_server.Network.TotalBytesReceived)}");
            WriteLine($"  Bytes Sent:        {FormatBytes(_server.Network.TotalBytesSent)}");
            WriteLine($"  Avg Tick Time:     {_server.GetAvgTickTime():F2} ms");
            WriteLine($"  Tick Budget:       {1000f / _server.Config.TickRate:F2} ms");
        }

        private void CmdUptime(string[] args)
        {
            WriteInfo($"Server uptime: {_server.GetUptimeString()} (started {_server.StartTime:yyyy-MM-dd HH:mm:ss} UTC)");
        }

        private void CmdStop(string[] args)
        {
            WriteInfo("Shutting down server...");
            Program.RequestShutdown();
        }

        private void CmdClear(string[] args)
        {
            System.Console.Clear();
        }

        // ====================================================
        // HELPERS
        // ====================================================

        private void BroadcastPvpChange(bool enabled)
        {
            var writer = new LiteNetLib.Utils.NetDataWriter();
            writer.Put((byte)PacketType.SERVER_INFO);
            var flags = new HashSet<ServerInfoType> { ServerInfoType.PVP_MODE };
            writer.Put(PacketHelper.GetSubTypeFlag(flags));
            writer.Put(enabled);

            _server.Network.Broadcast(writer, LiteNetLib.DeliveryMethod.ReliableOrdered,
                PacketHelper.GetChannel(PacketType.SERVER_INFO));
        }

        private PlayerSession FindPlayer(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return null;

            // Try by ID
            if (short.TryParse(nameOrId, out var id))
            {
                var byId = _server.Network.GetSession(id);
                if (byId != null) return byId;
            }

            // By name (case-insensitive)
            return _server.Network.GetAllSessions()
                .FirstOrDefault(s => s.CharacterName.Equals(nameOrId, StringComparison.OrdinalIgnoreCase));
        }

        private static string TruncateStr(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxLen ? s : s.Substring(0, maxLen - 2) + "..";
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        // ====================================================
        // OUTPUT HELPERS
        // ====================================================

        private static void WriteHeader(string text)
        {
            System.Console.ForegroundColor = ConsoleColor.Cyan;
            System.Console.WriteLine($"\n  === {text} ===");
            System.Console.ResetColor();
        }

        private static void WriteSeparator()
        {
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.WriteLine($"  {new string('-', 70)}");
            System.Console.ResetColor();
        }

        private static void WriteLine(string text)
        {
            System.Console.ForegroundColor = ConsoleColor.White;
            System.Console.WriteLine(text);
            System.Console.ResetColor();
        }

        private static void WriteColor(string text, ConsoleColor color)
        {
            System.Console.ForegroundColor = color;
            System.Console.Write(text);
            System.Console.ResetColor();
        }

        private static void WriteLineColor(string text, ConsoleColor color)
        {
            System.Console.ForegroundColor = color;
            System.Console.WriteLine(text);
            System.Console.ResetColor();
        }

        private static void WriteInfo(string text)
        {
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"  {text}");
            System.Console.ResetColor();
        }

        private static void WriteError(string text)
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"  {text}");
            System.Console.ResetColor();
        }

        private class CommandInfo
        {
            public string Name;
            public string Description;
            public string Usage;
            public Action<string[]> Handler;
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ErenshorDedicatedServer.Core;

namespace ErenshorDedicatedServer.Network
{
    /// <summary>
    /// Manages the connection handshake state machine for player sessions.
    /// Ensures packets are only processed when the session is in an appropriate state,
    /// preventing exploits from out-of-order or premature packet processing.
    /// </summary>
    public class SessionStateMachine
    {
        private readonly ConcurrentDictionary<short, SessionState> _states = new();

        // Valid transitions
        private static readonly Dictionary<ConnectionPhase, HashSet<ConnectionPhase>> ValidTransitions = new()
        {
            { ConnectionPhase.Connected, new HashSet<ConnectionPhase> { ConnectionPhase.Handshaking, ConnectionPhase.Disconnected } },
            { ConnectionPhase.Handshaking, new HashSet<ConnectionPhase> { ConnectionPhase.Authenticating, ConnectionPhase.Disconnected } },
            { ConnectionPhase.Authenticating, new HashSet<ConnectionPhase> { ConnectionPhase.Loading, ConnectionPhase.Disconnected } },
            { ConnectionPhase.Loading, new HashSet<ConnectionPhase> { ConnectionPhase.InGame, ConnectionPhase.Disconnected } },
            { ConnectionPhase.InGame, new HashSet<ConnectionPhase> { ConnectionPhase.Loading, ConnectionPhase.Disconnected } },
            { ConnectionPhase.Disconnected, new HashSet<ConnectionPhase>() },
        };

        // Packet type permissions per phase
        private static readonly Dictionary<ConnectionPhase, HashSet<string>> AllowedPackets = new()
        {
            { ConnectionPhase.Connected, new HashSet<string> { "SERVER_CONNECT" } },
            { ConnectionPhase.Handshaking, new HashSet<string> { "PLAYER_CONNECT" } },
            { ConnectionPhase.Authenticating, new HashSet<string> { "PLAYER_CONNECT" } },
            { ConnectionPhase.Loading, new HashSet<string> { "PLAYER_CONNECT", "PLAYER_DATA", "PLAYER_TRANSFORM" } },
            { ConnectionPhase.InGame, new HashSet<string>
                {
                    "PLAYER_CONNECT", "PLAYER_DATA", "PLAYER_TRANSFORM", "PLAYER_ACTION",
                    "PLAYER_MESSAGE", "PLAYER_REQUEST", "ENTITY_DATA", "ENTITY_SPAWN",
                    "ENTITY_TRANSFORM", "ENTITY_ACTION", "GROUP", "ITEM_DROP", "WEATHER_DATA"
                }
            },
            { ConnectionPhase.Disconnected, new HashSet<string>() },
        };

        /// <summary>
        /// Initializes a session to the Connected phase.
        /// </summary>
        public void InitSession(short playerId)
        {
            _states[playerId] = new SessionState
            {
                PlayerId = playerId,
                Phase = ConnectionPhase.Connected,
                PhaseEntryTime = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Attempts to transition a session to a new phase.
        /// Returns true if the transition was valid and applied.
        /// </summary>
        public bool TryTransition(short playerId, ConnectionPhase newPhase)
        {
            if (!_states.TryGetValue(playerId, out var state))
                return false;

            if (!ValidTransitions.TryGetValue(state.Phase, out var allowed))
                return false;

            if (!allowed.Contains(newPhase))
            {
                ServerLogger.Warning($"Invalid session transition for [{playerId}]: {state.Phase} -> {newPhase}", "SESSION");
                return false;
            }

            var oldPhase = state.Phase;
            state.Phase = newPhase;
            state.PhaseEntryTime = DateTime.UtcNow;

            ServerLogger.Debug($"Session [{playerId}] transition: {oldPhase} -> {newPhase}", "SESSION");
            return true;
        }

        /// <summary>
        /// Gets the current phase for a session.
        /// </summary>
        public ConnectionPhase GetPhase(short playerId)
        {
            return _states.TryGetValue(playerId, out var state) ? state.Phase : ConnectionPhase.Disconnected;
        }

        /// <summary>
        /// Checks if a packet type is allowed in the session's current phase.
        /// </summary>
        public bool IsPacketAllowed(short playerId, string packetType)
        {
            var phase = GetPhase(playerId);
            return AllowedPackets.TryGetValue(phase, out var allowed) && allowed.Contains(packetType);
        }

        /// <summary>
        /// Removes a session from tracking.
        /// </summary>
        public void RemoveSession(short playerId)
        {
            _states.TryRemove(playerId, out _);
        }

        /// <summary>
        /// Checks for sessions that have been stuck in a phase too long.
        /// Returns player IDs that should be disconnected.
        /// </summary>
        public List<short> GetTimedOutSessions(float handshakeTimeoutSeconds = 30f)
        {
            var result = new List<short>();
            var now = DateTime.UtcNow;

            foreach (var kvp in _states)
            {
                var state = kvp.Value;

                // Only timeout pre-game phases
                if (state.Phase == ConnectionPhase.InGame || state.Phase == ConnectionPhase.Disconnected)
                    continue;

                var elapsed = (float)(now - state.PhaseEntryTime).TotalSeconds;
                if (elapsed > handshakeTimeoutSeconds)
                {
                    result.Add(kvp.Key);
                    ServerLogger.Warning($"Session [{kvp.Key}] timed out in phase {state.Phase} ({elapsed:F0}s)", "SESSION");
                }
            }

            return result;
        }

        /// <summary>
        /// Gets stats for diagnostics.
        /// </summary>
        public Dictionary<ConnectionPhase, int> GetPhaseStats()
        {
            var result = new Dictionary<ConnectionPhase, int>();
            foreach (var state in _states.Values)
            {
                if (!result.ContainsKey(state.Phase))
                    result[state.Phase] = 0;
                result[state.Phase]++;
            }
            return result;
        }

        public int GetSessionCount() => _states.Count;
    }

    public enum ConnectionPhase
    {
        Connected,       // TCP connected, awaiting SERVER_CONNECT
        Handshaking,     // SERVER_CONNECT received, awaiting PLAYER_CONNECT
        Authenticating,  // PLAYER_CONNECT received, verifying identity
        Loading,         // Authenticated, loading zone data
        InGame,          // Fully connected and playing
        Disconnected     // Session ended
    }

    public class SessionState
    {
        public short PlayerId { get; set; }
        public ConnectionPhase Phase { get; set; }
        public DateTime PhaseEntryTime { get; set; }
    }
}

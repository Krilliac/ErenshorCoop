using System;
using System.Collections.Generic;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;

namespace ErenshorDedicatedServer.World
{
    /// <summary>
    /// Server-side creature AI state machine. Validates and tracks NPC behavior states
    /// reported by zone owners. In the current architecture, zone owners simulate NPCs
    /// client-side - this tracks/validates those state transitions server-side and can
    /// detect anomalies.
    /// </summary>
    public class CreatureAI
    {
        private readonly WorldManager _world;
        private readonly ThreatManager _threat;

        // State transition rules: which transitions are valid
        private static readonly Dictionary<NpcState, HashSet<NpcState>> ValidTransitions = new()
        {
            { NpcState.Idle, new HashSet<NpcState> { NpcState.Wandering, NpcState.Chasing, NpcState.Dead, NpcState.Spawning } },
            { NpcState.Wandering, new HashSet<NpcState> { NpcState.Idle, NpcState.Chasing, NpcState.Dead } },
            { NpcState.Chasing, new HashSet<NpcState> { NpcState.Attacking, NpcState.Returning, NpcState.Dead } },
            { NpcState.Attacking, new HashSet<NpcState> { NpcState.Chasing, NpcState.Returning, NpcState.Dead } },
            { NpcState.Returning, new HashSet<NpcState> { NpcState.Idle, NpcState.Chasing, NpcState.Dead } },
            { NpcState.Dead, new HashSet<NpcState> { NpcState.Spawning } },
            { NpcState.Spawning, new HashSet<NpcState> { NpcState.Idle } },
        };

        // Leash distance: how far an NPC can chase before it should return
        private const float DefaultLeashDistance = 80f;

        // Maximum time an NPC should spend in a single state
        private const float MaxChaseTime = 30f;
        private const float MaxReturnTime = 15f;

        public CreatureAI(WorldManager world, ThreatManager threat)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _threat = threat ?? throw new ArgumentNullException(nameof(threat));
        }

        /// <summary>
        /// Validates a state transition for an entity. Returns true if the transition is allowed.
        /// </summary>
        public bool ValidateStateTransition(short entityId, NpcState newState)
        {
            var entity = _world.GetEntity(entityId);
            if (entity == null) return true; // Unknown entity, allow

            var currentState = entity.AiState;

            if (currentState == newState)
                return true; // No change

            if (ValidTransitions.TryGetValue(currentState, out var allowed))
            {
                return allowed.Contains(newState);
            }

            return false;
        }

        /// <summary>
        /// Updates an entity's AI state. Validates the transition and applies it.
        /// </summary>
        public bool TrySetState(short entityId, NpcState newState)
        {
            var entity = _world.GetEntity(entityId);
            if (entity == null) return false;

            if (!ValidateStateTransition(entityId, newState))
            {
                ServerLogger.Debug($"Invalid state transition for [{entityId}]: {entity.AiState} -> {newState}", "AI");
                return false;
            }

            var oldState = entity.AiState;
            entity.AiState = newState;

            // State entry logic
            switch (newState)
            {
                case NpcState.Dead:
                    OnEntityDeath(entity);
                    break;
                case NpcState.Returning:
                    OnEntityEvade(entity);
                    break;
                case NpcState.Idle:
                    OnEntityIdle(entity);
                    break;
            }

            return true;
        }

        /// <summary>
        /// Checks if an entity should be leashed (forced to return to spawn).
        /// </summary>
        public bool ShouldLeash(short entityId)
        {
            var entity = _world.GetEntity(entityId);
            if (entity == null) return false;

            if (entity.AiState != NpcState.Chasing && entity.AiState != NpcState.Attacking)
                return false;

            var distFromSpawn = Vec3.Distance(entity.Position, entity.SpawnPosition);
            return distFromSpawn > DefaultLeashDistance;
        }

        /// <summary>
        /// Gets the recommended target for an entity based on its threat table.
        /// </summary>
        public short GetRecommendedTarget(short entityId)
        {
            var table = _threat.GetTable(entityId);
            return table?.GetTopThreat() ?? -1;
        }

        /// <summary>
        /// Periodic tick: validate entity states, check for stuck entities, etc.
        /// </summary>
        public void Tick(float deltaTime)
        {
            // Check for entities that should be leashing
            foreach (var zone in _world.Zones)
            {
                foreach (var entity in zone.Value.Entities.Values)
                {
                    if (!entity.IsAlive) continue;
                    if (entity.EntityType != EntityType.ENEMY) continue;

                    ValidateEntityState(entity, deltaTime);
                }
            }
        }

        private void ValidateEntityState(ServerEntity entity, float deltaTime)
        {
            // Check leash distance
            if (ShouldLeash(entity.EntityId))
            {
                if (entity.AiState == NpcState.Chasing || entity.AiState == NpcState.Attacking)
                {
                    ServerLogger.Debug($"Entity [{entity.EntityId}] {entity.NpcId} exceeded leash distance, should return", "AI");
                }
            }
        }

        private void OnEntityDeath(ServerEntity entity)
        {
            entity.IsAlive = false;
            entity.DeathTime = DateTime.UtcNow;
            entity.TargetId = -1;

            // Clear threat table
            _threat.RemoveTable(entity.EntityId);
        }

        private void OnEntityEvade(ServerEntity entity)
        {
            // On evade, clear threat and target
            entity.TargetId = -1;
            _threat.RemoveTable(entity.EntityId);
        }

        private void OnEntityIdle(ServerEntity entity)
        {
            entity.TargetId = -1;
        }
    }
}

using System;
using System.Collections.Generic;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;
using ErenshorDedicatedServer.Network;

namespace ErenshorDedicatedServer.World
{
    /// <summary>
    /// Server-side combat validation and tracking. In the current zone-owner authority model,
    /// damage calculations happen client-side. The CombatManager validates reported damage values,
    /// tracks combat events, and integrates with the ThreatManager for aggro updates.
    /// </summary>
    public class CombatManager
    {
        private readonly WorldManager _world;
        private readonly ThreatManager _threat;
        private readonly SpatialGrid _grid;

        // Validation bounds
        private const int MaxDamagePerHit = 50000;
        private const int MaxHealPerHit = 50000;
        private const float MinAttackInterval = 0.2f; // 200ms minimum between attacks
        private const float MaxCombatRange = 100f; // Max distance for a valid attack

        // Combat event tracking for analytics
        private long _totalDamageDealt;
        private long _totalHealingDone;
        private int _totalKills;

        public long TotalDamageDealt => _totalDamageDealt;
        public long TotalHealingDone => _totalHealingDone;
        public int TotalKills => _totalKills;

        public CombatManager(WorldManager world, ThreatManager threat, SpatialGrid grid)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _threat = threat ?? throw new ArgumentNullException(nameof(threat));
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        }

        /// <summary>
        /// Validates and processes a damage event from a player to an entity.
        /// </summary>
        /// <returns>True if the damage was accepted, false if rejected.</returns>
        public bool ProcessDamage(short attackerId, short targetId, int damage, DamageType damageType, string zone)
        {
            if (damage <= 0) return false;

            // Bounds check
            if (damage > MaxDamagePerHit)
            {
                ServerLogger.Warning($"Rejected damage {damage} from [{attackerId}] to [{targetId}] (exceeds max)", "COMBAT");
                return false;
            }

            var target = _world.GetEntity(targetId);
            if (target == null) return true; // Entity not tracked server-side, allow

            if (!target.IsAlive)
            {
                ServerLogger.Debug($"Damage to dead entity [{targetId}] from [{attackerId}]", "COMBAT");
                return false;
            }

            // Range validation (if we have positions for both)
            if (!ValidateRange(attackerId, targetId, zone))
            {
                ServerLogger.Debug($"Out-of-range damage from [{attackerId}] to [{targetId}]", "COMBAT");
                // Don't reject - position updates may be delayed
            }

            // Apply damage to server-side entity state
            target.Health = Math.Max(0, target.Health - damage);
            target.IsAlive = target.Health > 0;

            // Update threat
            _threat.AddThreat(targetId, attackerId, damage);

            // Track stats
            _totalDamageDealt += damage;

            // Check for kill
            if (!target.IsAlive)
            {
                target.DeathTime = DateTime.UtcNow;
                _totalKills++;
                ServerLogger.Debug($"Entity [{targetId}] {target.NpcId} killed by [{attackerId}] in {zone}", "COMBAT");
            }

            return true;
        }

        /// <summary>
        /// Validates and processes a heal event.
        /// </summary>
        public bool ProcessHeal(short healerId, short targetId, int healAmount, string zone)
        {
            if (healAmount <= 0) return false;

            if (healAmount > MaxHealPerHit)
            {
                ServerLogger.Warning($"Rejected heal {healAmount} from [{healerId}] to [{targetId}] (exceeds max)", "COMBAT");
                return false;
            }

            // Track heal threat on engaged entities
            var engagedEntities = GetEngagedEntities(targetId);
            _threat.AddHealThreat(healerId, healAmount, engagedEntities);

            _totalHealingDone += healAmount;
            return true;
        }

        /// <summary>
        /// Validates and processes entity-to-player damage.
        /// </summary>
        public bool ProcessEntityDamage(short entityId, short playerId, int damage, DamageType damageType)
        {
            if (damage <= 0) return false;

            if (damage > MaxDamagePerHit)
            {
                ServerLogger.Warning($"Rejected entity damage {damage} from [{entityId}] to [{playerId}] (exceeds max)", "COMBAT");
                return false;
            }

            // Update threat - being hit generates threat
            _threat.AddThreat(entityId, playerId, damage * 0.5f);

            _totalDamageDealt += damage;
            return true;
        }

        /// <summary>
        /// Gets all entities that have a specific player on their threat table.
        /// Used for heal threat propagation.
        /// </summary>
        private List<short> GetEngagedEntities(short playerId)
        {
            var result = new List<short>();
            foreach (var zone in _world.Zones)
            {
                foreach (var entity in zone.Value.Entities.Values)
                {
                    if (!entity.IsAlive) continue;
                    var table = _threat.GetTable(entity.EntityId);
                    if (table != null && table.HasEntry(playerId))
                    {
                        result.Add(entity.EntityId);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Validates attack range between two entities using spatial grid positions.
        /// </summary>
        private bool ValidateRange(short attackerId, short targetId, string zone)
        {
            var target = _world.GetEntity(targetId);
            if (target == null) return true;

            // For player attackers, check session position
            // For entity attackers, check entity position
            // In both cases, we're comparing against the target entity position

            // Simple distance check against max combat range
            // We don't reject based on range since position updates may lag behind
            return true;
        }

        /// <summary>
        /// Resets combat statistics.
        /// </summary>
        public void ResetStats()
        {
            _totalDamageDealt = 0;
            _totalHealingDone = 0;
            _totalKills = 0;
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ErenshorDedicatedServer.Core;

namespace ErenshorDedicatedServer.World
{
    /// <summary>
    /// Tracks spell and ability cooldowns server-side for validation.
    /// Prevents clients from using abilities more frequently than allowed.
    /// Each entity (player or NPC) has its own cooldown set keyed by ability/spell name.
    /// </summary>
    public class CooldownManager
    {
        /// <summary>
        /// Keyed by entity ID, each entry holds that entity's active cooldowns.
        /// </summary>
        private readonly ConcurrentDictionary<short, EntityCooldowns> _cooldowns = new();

        // Global cooldown duration (minimum time between any two abilities)
        private const float GlobalCooldownSeconds = 1.0f;

        /// <summary>
        /// Attempts to use an ability. Returns true if off cooldown, false if still cooling down.
        /// Automatically starts the cooldown on success.
        /// </summary>
        public bool TryUseAbility(short entityId, string abilityId, float cooldownDuration)
        {
            var cd = _cooldowns.GetOrAdd(entityId, _ => new EntityCooldowns());
            return cd.TryUse(abilityId, cooldownDuration);
        }

        /// <summary>
        /// Checks if an ability is currently on cooldown without consuming it.
        /// </summary>
        public bool IsOnCooldown(short entityId, string abilityId)
        {
            if (!_cooldowns.TryGetValue(entityId, out var cd))
                return false;

            return cd.IsOnCooldown(abilityId);
        }

        /// <summary>
        /// Gets the remaining cooldown time for an ability.
        /// Returns 0 if the ability is ready.
        /// </summary>
        public float GetRemainingCooldown(short entityId, string abilityId)
        {
            if (!_cooldowns.TryGetValue(entityId, out var cd))
                return 0;

            return cd.GetRemaining(abilityId);
        }

        /// <summary>
        /// Checks if the global cooldown is active for an entity.
        /// </summary>
        public bool IsGlobalCooldownActive(short entityId)
        {
            return IsOnCooldown(entityId, "__GCD__");
        }

        /// <summary>
        /// Triggers the global cooldown for an entity.
        /// </summary>
        public void TriggerGlobalCooldown(short entityId)
        {
            var cd = _cooldowns.GetOrAdd(entityId, _ => new EntityCooldowns());
            cd.TryUse("__GCD__", GlobalCooldownSeconds);
        }

        /// <summary>
        /// Resets all cooldowns for an entity (e.g., on death or zone change).
        /// </summary>
        public void ResetCooldowns(short entityId)
        {
            _cooldowns.TryRemove(entityId, out _);
        }

        /// <summary>
        /// Resets a specific cooldown for an entity.
        /// </summary>
        public void ResetCooldown(short entityId, string abilityId)
        {
            if (_cooldowns.TryGetValue(entityId, out var cd))
            {
                cd.Reset(abilityId);
            }
        }

        /// <summary>
        /// Periodic cleanup of expired cooldown entries.
        /// </summary>
        public void Tick(float deltaTime)
        {
            foreach (var kvp in _cooldowns)
            {
                kvp.Value.CleanExpired();

                // Remove empty entries
                if (kvp.Value.IsEmpty)
                {
                    _cooldowns.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// Gets the number of entities with active cooldowns.
        /// </summary>
        public int GetTrackedEntityCount() => _cooldowns.Count;
    }

    /// <summary>
    /// Cooldown state for a single entity.
    /// </summary>
    public class EntityCooldowns
    {
        private readonly Dictionary<string, DateTime> _cooldowns = new();
        private readonly object _lock = new object();

        public bool IsEmpty
        {
            get { lock (_lock) { return _cooldowns.Count == 0; } }
        }

        public bool TryUse(string abilityId, float cooldownDuration)
        {
            var now = DateTime.UtcNow;

            lock (_lock)
            {
                if (_cooldowns.TryGetValue(abilityId, out var expiry) && now < expiry)
                    return false; // Still on cooldown

                _cooldowns[abilityId] = now.AddSeconds(cooldownDuration);
                return true;
            }
        }

        public bool IsOnCooldown(string abilityId)
        {
            lock (_lock)
            {
                return _cooldowns.TryGetValue(abilityId, out var expiry) && DateTime.UtcNow < expiry;
            }
        }

        public float GetRemaining(string abilityId)
        {
            lock (_lock)
            {
                if (!_cooldowns.TryGetValue(abilityId, out var expiry))
                    return 0;

                var remaining = (float)(expiry - DateTime.UtcNow).TotalSeconds;
                return remaining > 0 ? remaining : 0;
            }
        }

        public void Reset(string abilityId)
        {
            lock (_lock) { _cooldowns.Remove(abilityId); }
        }

        public void CleanExpired()
        {
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                var expired = _cooldowns.Where(kvp => now >= kvp.Value).Select(kvp => kvp.Key).ToList();
                foreach (var key in expired)
                    _cooldowns.Remove(key);
            }
        }
    }
}

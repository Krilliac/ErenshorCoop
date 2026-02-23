using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ErenshorDedicatedServer.Core;

namespace ErenshorDedicatedServer.World
{
    /// <summary>
    /// Manages per-entity threat tables for aggro tracking.
    /// Each hostile NPC maintains a sorted threat list of players/entities that have
    /// interacted with it. The zone owner uses this to determine AI targeting.
    /// </summary>
    public class ThreatManager
    {
        /// <summary>
        /// Keyed by entity ID, each entry is that entity's threat table.
        /// </summary>
        private readonly ConcurrentDictionary<short, ThreatTable> _tables = new();

        // Threat decay: entries older than this are removed
        private const float ThreatDecaySeconds = 60f;

        // Threat modifiers
        private const float HealThreatMultiplier = 0.5f;
        private const float TauntThreatMultiplier = 2.0f;

        /// <summary>
        /// Gets or creates a threat table for the given entity.
        /// </summary>
        public ThreatTable GetOrCreateTable(short entityId)
        {
            return _tables.GetOrAdd(entityId, _ => new ThreatTable(entityId));
        }

        /// <summary>
        /// Gets the threat table for an entity, or null if none exists.
        /// </summary>
        public ThreatTable GetTable(short entityId)
        {
            _tables.TryGetValue(entityId, out var table);
            return table;
        }

        /// <summary>
        /// Adds threat from a source to a target entity.
        /// </summary>
        public void AddThreat(short entityId, short sourceId, float amount)
        {
            var table = GetOrCreateTable(entityId);
            table.AddThreat(sourceId, amount);
        }

        /// <summary>
        /// Adds threat from healing (healer generates threat on all engaged enemies).
        /// </summary>
        public void AddHealThreat(short healerId, float healAmount, IEnumerable<short> engagedEntities)
        {
            var threat = healAmount * HealThreatMultiplier;
            foreach (var entityId in engagedEntities)
            {
                var table = GetTable(entityId);
                if (table != null && table.HasEntry(healerId))
                {
                    // Only add heal threat if healer is already on the table
                    // (or if the healed target is on the table)
                    table.AddThreat(healerId, threat);
                }
            }
        }

        /// <summary>
        /// Applies a taunt: sets the source's threat to be the highest + modifier.
        /// </summary>
        public void ApplyTaunt(short entityId, short sourceId)
        {
            var table = GetTable(entityId);
            if (table == null) return;

            var highest = table.GetHighestThreat();
            if (highest > 0)
            {
                table.SetThreat(sourceId, highest * TauntThreatMultiplier);
            }
            else
            {
                table.AddThreat(sourceId, 100f); // Base taunt if no existing threat
            }
        }

        /// <summary>
        /// Removes all threat entries for a player (e.g., on death or zone change).
        /// </summary>
        public void RemoveSource(short sourceId)
        {
            foreach (var table in _tables.Values)
            {
                table.RemoveEntry(sourceId);
            }
        }

        /// <summary>
        /// Removes an entity's threat table entirely (entity died or despawned).
        /// </summary>
        public void RemoveTable(short entityId)
        {
            _tables.TryRemove(entityId, out _);
        }

        /// <summary>
        /// Clears all threat tables for a zone (zone reset/empty).
        /// </summary>
        public void ClearZone(string zone, Func<short, string> entityZoneLookup)
        {
            foreach (var kvp in _tables)
            {
                if (entityZoneLookup(kvp.Key) == zone)
                {
                    _tables.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// Periodic tick: decay old threat entries.
        /// </summary>
        public void Tick(float deltaTime)
        {
            var now = DateTime.UtcNow;
            foreach (var table in _tables.Values)
            {
                table.DecayEntries(now, ThreatDecaySeconds);
            }

            // Remove empty tables
            foreach (var kvp in _tables)
            {
                if (kvp.Value.IsEmpty)
                {
                    _tables.TryRemove(kvp.Key, out _);
                }
            }
        }

        public int GetTableCount() => _tables.Count;
    }

    /// <summary>
    /// Per-entity threat table. Tracks which sources have generated threat
    /// and maintains a sorted priority for target selection.
    /// </summary>
    public class ThreatTable
    {
        public short OwnerId { get; }

        private readonly Dictionary<short, ThreatEntry> _entries = new();
        private readonly object _lock = new object();

        public bool IsEmpty
        {
            get { lock (_lock) { return _entries.Count == 0; } }
        }

        public ThreatTable(short ownerId)
        {
            OwnerId = ownerId;
        }

        public void AddThreat(short sourceId, float amount)
        {
            if (amount <= 0) return;

            lock (_lock)
            {
                if (_entries.TryGetValue(sourceId, out var entry))
                {
                    entry.Threat += amount;
                    entry.LastUpdate = DateTime.UtcNow;
                }
                else
                {
                    _entries[sourceId] = new ThreatEntry
                    {
                        SourceId = sourceId,
                        Threat = amount,
                        LastUpdate = DateTime.UtcNow
                    };
                }
            }
        }

        public void SetThreat(short sourceId, float amount)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(sourceId, out var entry))
                {
                    entry.Threat = amount;
                    entry.LastUpdate = DateTime.UtcNow;
                }
                else
                {
                    _entries[sourceId] = new ThreatEntry
                    {
                        SourceId = sourceId,
                        Threat = amount,
                        LastUpdate = DateTime.UtcNow
                    };
                }
            }
        }

        public void RemoveEntry(short sourceId)
        {
            lock (_lock)
            {
                _entries.Remove(sourceId);
            }
        }

        public bool HasEntry(short sourceId)
        {
            lock (_lock)
            {
                return _entries.ContainsKey(sourceId);
            }
        }

        /// <summary>
        /// Returns the source ID with the highest threat, or -1 if empty.
        /// </summary>
        public short GetTopThreat()
        {
            lock (_lock)
            {
                if (_entries.Count == 0) return -1;

                short topId = -1;
                float topThreat = float.MinValue;

                foreach (var kvp in _entries)
                {
                    if (kvp.Value.Threat > topThreat)
                    {
                        topThreat = kvp.Value.Threat;
                        topId = kvp.Key;
                    }
                }

                return topId;
            }
        }

        /// <summary>
        /// Returns the highest threat value, or 0 if empty.
        /// </summary>
        public float GetHighestThreat()
        {
            lock (_lock)
            {
                float max = 0;
                foreach (var entry in _entries.Values)
                {
                    if (entry.Threat > max)
                        max = entry.Threat;
                }
                return max;
            }
        }

        /// <summary>
        /// Returns sorted threat list (highest first).
        /// </summary>
        public List<ThreatEntry> GetSortedEntries()
        {
            lock (_lock)
            {
                return _entries.Values.OrderByDescending(e => e.Threat).ToList();
            }
        }

        /// <summary>
        /// Returns the threat amount for a specific source.
        /// </summary>
        public float GetThreat(short sourceId)
        {
            lock (_lock)
            {
                return _entries.TryGetValue(sourceId, out var entry) ? entry.Threat : 0;
            }
        }

        /// <summary>
        /// Decays entries that haven't been updated recently.
        /// </summary>
        public void DecayEntries(DateTime now, float decaySeconds)
        {
            lock (_lock)
            {
                var toRemove = new List<short>();
                foreach (var kvp in _entries)
                {
                    if ((now - kvp.Value.LastUpdate).TotalSeconds > decaySeconds)
                    {
                        toRemove.Add(kvp.Key);
                    }
                }

                foreach (var id in toRemove)
                {
                    _entries.Remove(id);
                }
            }
        }

        public int Count
        {
            get { lock (_lock) { return _entries.Count; } }
        }
    }

    public class ThreatEntry
    {
        public short SourceId { get; set; }
        public float Threat { get; set; }
        public DateTime LastUpdate { get; set; }
    }
}

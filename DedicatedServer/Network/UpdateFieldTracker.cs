using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ErenshorDedicatedServer.Data;

namespace ErenshorDedicatedServer.Network
{
    /// <summary>
    /// Tracks which fields have changed ("dirty") for each entity since the last network update.
    /// Only dirty fields are included in outgoing packets, saving bandwidth.
    /// Each connected player has their own dirty mask per entity, since different players
    /// may have received different updates.
    /// </summary>
    public class UpdateFieldTracker
    {
        /// <summary>
        /// Per-receiver, per-entity dirty flags.
        /// Key: receiver player ID -> entity ID -> dirty field mask.
        /// </summary>
        private readonly ConcurrentDictionary<short, ConcurrentDictionary<short, uint>> _dirtyMasks = new();

        // Field bit positions for entities
        public const uint FIELD_POSITION = 1 << 0;
        public const uint FIELD_ROTATION = 1 << 1;
        public const uint FIELD_HEALTH = 1 << 2;
        public const uint FIELD_MANA = 1 << 3;
        public const uint FIELD_TARGET = 1 << 4;
        public const uint FIELD_ANIM = 1 << 5;
        public const uint FIELD_GEAR = 1 << 6;
        public const uint FIELD_LEVEL = 1 << 7;
        public const uint FIELD_CLASS = 1 << 8;
        public const uint FIELD_ZONE = 1 << 9;
        public const uint FIELD_NAME = 1 << 10;
        public const uint FIELD_STATE = 1 << 11;
        public const uint FIELD_ALL = 0xFFFFFFFF;

        /// <summary>
        /// Marks a field as dirty for all receivers.
        /// </summary>
        public void MarkDirty(short entityId, uint fieldMask)
        {
            foreach (var receiverMasks in _dirtyMasks.Values)
            {
                receiverMasks.AddOrUpdate(entityId,
                    fieldMask,
                    (_, existing) => existing | fieldMask);
            }
        }

        /// <summary>
        /// Marks a field as dirty for a specific receiver.
        /// </summary>
        public void MarkDirtyForReceiver(short receiverId, short entityId, uint fieldMask)
        {
            var receiverMasks = _dirtyMasks.GetOrAdd(receiverId, _ => new ConcurrentDictionary<short, uint>());
            receiverMasks.AddOrUpdate(entityId,
                fieldMask,
                (_, existing) => existing | fieldMask);
        }

        /// <summary>
        /// Marks all fields dirty for a specific receiver (e.g., new player needs full state).
        /// </summary>
        public void MarkAllDirtyForReceiver(short receiverId, IEnumerable<short> entityIds)
        {
            var receiverMasks = _dirtyMasks.GetOrAdd(receiverId, _ => new ConcurrentDictionary<short, uint>());
            foreach (var entityId in entityIds)
            {
                receiverMasks[entityId] = FIELD_ALL;
            }
        }

        /// <summary>
        /// Gets the dirty mask for an entity from a receiver's perspective.
        /// Returns 0 if nothing is dirty.
        /// </summary>
        public uint GetDirtyMask(short receiverId, short entityId)
        {
            if (!_dirtyMasks.TryGetValue(receiverId, out var receiverMasks))
                return 0;

            receiverMasks.TryGetValue(entityId, out var mask);
            return mask;
        }

        /// <summary>
        /// Clears the dirty mask for an entity from a receiver's perspective.
        /// Call after successfully sending the update.
        /// </summary>
        public void ClearDirty(short receiverId, short entityId)
        {
            if (_dirtyMasks.TryGetValue(receiverId, out var receiverMasks))
            {
                receiverMasks.TryRemove(entityId, out _);
            }
        }

        /// <summary>
        /// Clears specific fields from the dirty mask.
        /// </summary>
        public void ClearDirtyFields(short receiverId, short entityId, uint fieldMask)
        {
            if (_dirtyMasks.TryGetValue(receiverId, out var receiverMasks))
            {
                if (receiverMasks.TryGetValue(entityId, out var existing))
                {
                    var newMask = existing & ~fieldMask;
                    if (newMask == 0)
                        receiverMasks.TryRemove(entityId, out _);
                    else
                        receiverMasks[entityId] = newMask;
                }
            }
        }

        /// <summary>
        /// Checks if any fields are dirty for a receiver.
        /// </summary>
        public bool HasDirtyFields(short receiverId, short entityId)
        {
            return GetDirtyMask(receiverId, entityId) != 0;
        }

        /// <summary>
        /// Gets all entity IDs with dirty fields for a receiver.
        /// </summary>
        public List<(short entityId, uint mask)> GetAllDirty(short receiverId)
        {
            var result = new List<(short, uint)>();

            if (!_dirtyMasks.TryGetValue(receiverId, out var receiverMasks))
                return result;

            foreach (var kvp in receiverMasks)
            {
                if (kvp.Value != 0)
                    result.Add((kvp.Key, kvp.Value));
            }

            return result;
        }

        /// <summary>
        /// Registers a new receiver (player). Marks all existing entities as fully dirty.
        /// </summary>
        public void RegisterReceiver(short receiverId)
        {
            _dirtyMasks.GetOrAdd(receiverId, _ => new ConcurrentDictionary<short, uint>());
        }

        /// <summary>
        /// Unregisters a receiver (player disconnected).
        /// </summary>
        public void UnregisterReceiver(short receiverId)
        {
            _dirtyMasks.TryRemove(receiverId, out _);
        }

        /// <summary>
        /// Removes all dirty tracking for an entity (entity destroyed).
        /// </summary>
        public void RemoveEntity(short entityId)
        {
            foreach (var receiverMasks in _dirtyMasks.Values)
            {
                receiverMasks.TryRemove(entityId, out _);
            }
        }

        /// <summary>
        /// Gets stats for diagnostics.
        /// </summary>
        public (int receivers, int totalDirtyEntries) GetStats()
        {
            int total = 0;
            foreach (var receiverMasks in _dirtyMasks.Values)
            {
                total += receiverMasks.Count;
            }
            return (_dirtyMasks.Count, total);
        }
    }
}

using System;
using System.Collections.Generic;
using ErenshorDedicatedServer.Data;

namespace ErenshorDedicatedServer.World
{
    /// <summary>
    /// Spatial partitioning grid for efficient proximity queries.
    /// Divides the world into fixed-size cells and tracks entity positions
    /// for fast neighbor lookups. Used to optimize broadcasts - only send
    /// entity updates to players within relevant cells.
    /// </summary>
    public class SpatialGrid
    {
        private readonly float _cellSize;
        private readonly Dictionary<long, GridCell> _cells = new();
        private readonly Dictionary<short, GridEntry> _entries = new();
        private readonly object _lock = new object();

        /// <summary>
        /// Creates a new spatial grid with the given cell size.
        /// </summary>
        /// <param name="cellSize">Size of each cell in world units. Typical: 50-100.</param>
        public SpatialGrid(float cellSize = 64f)
        {
            _cellSize = cellSize > 0 ? cellSize : 64f;
        }

        /// <summary>
        /// Inserts or updates an entity's position in the grid.
        /// </summary>
        public void UpdatePosition(short entityId, Vec3 position, string zone)
        {
            var newCellKey = GetCellKey(position);

            lock (_lock)
            {
                if (_entries.TryGetValue(entityId, out var existing))
                {
                    if (existing.CellKey == newCellKey)
                    {
                        // Same cell, just update position
                        existing.Position = position;
                        return;
                    }

                    // Remove from old cell
                    if (_cells.TryGetValue(existing.CellKey, out var oldCell))
                    {
                        oldCell.Entities.Remove(entityId);
                        if (oldCell.Entities.Count == 0)
                            _cells.Remove(existing.CellKey);
                    }

                    // Update entry
                    existing.Position = position;
                    existing.Zone = zone;
                    existing.CellKey = newCellKey;
                }
                else
                {
                    // New entry
                    _entries[entityId] = new GridEntry
                    {
                        EntityId = entityId,
                        Position = position,
                        Zone = zone,
                        CellKey = newCellKey
                    };
                }

                // Add to new cell
                if (!_cells.TryGetValue(newCellKey, out var cell))
                {
                    cell = new GridCell { Key = newCellKey };
                    _cells[newCellKey] = cell;
                }
                cell.Entities[entityId] = _entries[entityId];
            }
        }

        /// <summary>
        /// Removes an entity from the grid.
        /// </summary>
        public void Remove(short entityId)
        {
            lock (_lock)
            {
                if (!_entries.TryGetValue(entityId, out var entry))
                    return;

                if (_cells.TryGetValue(entry.CellKey, out var cell))
                {
                    cell.Entities.Remove(entityId);
                    if (cell.Entities.Count == 0)
                        _cells.Remove(entry.CellKey);
                }

                _entries.Remove(entityId);
            }
        }

        /// <summary>
        /// Finds all entities within radius of a position, optionally filtered by zone.
        /// </summary>
        public List<short> FindNearby(Vec3 position, float radius, string zone = null)
        {
            var result = new List<short>();
            var radiusSq = radius * radius;

            // Determine which cells could contain entities within radius
            var cellRadius = (int)Math.Ceiling(radius / _cellSize);
            var centerCellX = (int)Math.Floor(position.X / _cellSize);
            var centerCellZ = (int)Math.Floor(position.Z / _cellSize);

            lock (_lock)
            {
                for (int dx = -cellRadius; dx <= cellRadius; dx++)
                {
                    for (int dz = -cellRadius; dz <= cellRadius; dz++)
                    {
                        var key = PackCellKey(centerCellX + dx, centerCellZ + dz);
                        if (!_cells.TryGetValue(key, out var cell))
                            continue;

                        foreach (var entry in cell.Entities.Values)
                        {
                            if (zone != null && entry.Zone != zone)
                                continue;

                            var distSq = Vec3.DistanceSquared(position, entry.Position);
                            if (distSq <= radiusSq)
                                result.Add(entry.EntityId);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Finds all entities in the same zone within radius of a position.
        /// Returns entity IDs excluding the source entity.
        /// </summary>
        public List<short> FindNearbyExcluding(Vec3 position, float radius, string zone, short excludeId)
        {
            var result = FindNearby(position, radius, zone);
            result.Remove(excludeId);
            return result;
        }

        /// <summary>
        /// Gets all entities in a specific cell.
        /// </summary>
        public List<short> GetEntitiesInCell(Vec3 position)
        {
            var key = GetCellKey(position);
            var result = new List<short>();

            lock (_lock)
            {
                if (_cells.TryGetValue(key, out var cell))
                {
                    result.AddRange(cell.Entities.Keys);
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the number of active cells.
        /// </summary>
        public int GetCellCount()
        {
            lock (_lock) { return _cells.Count; }
        }

        /// <summary>
        /// Gets the total number of tracked entities.
        /// </summary>
        public int GetEntityCount()
        {
            lock (_lock) { return _entries.Count; }
        }

        /// <summary>
        /// Clears all entries for a zone.
        /// </summary>
        public void ClearZone(string zone)
        {
            lock (_lock)
            {
                var toRemove = new List<short>();
                foreach (var kvp in _entries)
                {
                    if (kvp.Value.Zone == zone)
                        toRemove.Add(kvp.Key);
                }

                foreach (var id in toRemove)
                {
                    if (_entries.TryGetValue(id, out var entry))
                    {
                        if (_cells.TryGetValue(entry.CellKey, out var cell))
                        {
                            cell.Entities.Remove(id);
                            if (cell.Entities.Count == 0)
                                _cells.Remove(entry.CellKey);
                        }
                        _entries.Remove(id);
                    }
                }
            }
        }

        private long GetCellKey(Vec3 position)
        {
            var cellX = (int)Math.Floor(position.X / _cellSize);
            var cellZ = (int)Math.Floor(position.Z / _cellSize);
            return PackCellKey(cellX, cellZ);
        }

        private static long PackCellKey(int x, int z)
        {
            return ((long)x << 32) | (uint)z;
        }
    }

    public class GridCell
    {
        public long Key { get; set; }
        public Dictionary<short, GridEntry> Entities { get; } = new();
    }

    public class GridEntry
    {
        public short EntityId { get; set; }
        public Vec3 Position { get; set; }
        public string Zone { get; set; } = "";
        public long CellKey { get; set; }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;

namespace ErenshorDedicatedServer.World
{
    /// <summary>
    /// Server-side movement validation and prediction.
    /// Tracks expected movement patterns for entities and validates incoming position
    /// updates from zone owners. Can also generate basic movement for server-authoritative
    /// entities (e.g., during zone owner transitions).
    /// </summary>
    public class MovementGenerator
    {
        private readonly WorldManager _world;
        private readonly ConcurrentDictionary<short, MovementState> _states = new();

        // Movement speed bounds (units/second)
        private const float DefaultWalkSpeed = 3.5f;
        private const float DefaultRunSpeed = 7.0f;
        private const float MaxPlayerSpeed = 15.0f;
        private const float MaxNpcSpeed = 12.0f;

        // Validation tolerance
        private const float PositionTolerance = 5.0f; // Allow 5 unit deviation
        private const float TeleportThreshold = 100f; // Above this = teleport (zone change etc.)

        public MovementGenerator(WorldManager world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        /// <summary>
        /// Updates the movement state for an entity.
        /// </summary>
        public void UpdateMovement(short entityId, Vec3 newPosition, MovementType type)
        {
            var state = _states.GetOrAdd(entityId, _ => new MovementState());

            var now = DateTime.UtcNow;
            var delta = (float)(now - state.LastUpdate).TotalSeconds;

            state.PreviousPosition = state.CurrentPosition;
            state.CurrentPosition = newPosition;
            state.LastUpdate = now;
            state.Type = type;

            if (delta > 0 && delta < 5f) // Ignore very old updates
            {
                var dist = Vec3.Distance(state.PreviousPosition, newPosition);
                state.CurrentSpeed = dist / delta;
            }
        }

        /// <summary>
        /// Validates a position update. Returns validation result with details.
        /// </summary>
        public MovementValidation ValidatePosition(short entityId, Vec3 newPosition, bool isPlayer)
        {
            if (!_states.TryGetValue(entityId, out var state))
            {
                // First update for this entity - always accept
                return new MovementValidation { IsValid = true };
            }

            var now = DateTime.UtcNow;
            var delta = (float)(now - state.LastUpdate).TotalSeconds;

            if (delta <= 0 || delta > 10f)
                return new MovementValidation { IsValid = true }; // Stale data, accept

            var distance = Vec3.Distance(state.CurrentPosition, newPosition);

            // Teleport detection (zone change, etc.)
            if (distance > TeleportThreshold)
            {
                return new MovementValidation
                {
                    IsValid = true,
                    IsTeleport = true,
                    Distance = distance
                };
            }

            // Speed check
            var speed = distance / delta;
            var maxSpeed = isPlayer ? MaxPlayerSpeed : MaxNpcSpeed;

            // Allow burst speed for short periods (jumping, knockback, etc.)
            var speedLimit = maxSpeed * 1.5f;

            if (speed > speedLimit)
            {
                return new MovementValidation
                {
                    IsValid = false,
                    Reason = $"Speed violation: {speed:F1} > {speedLimit:F1}",
                    Distance = distance,
                    Speed = speed
                };
            }

            return new MovementValidation
            {
                IsValid = true,
                Distance = distance,
                Speed = speed
            };
        }

        /// <summary>
        /// Generates a chase movement position (straight-line intercept).
        /// Used when the server needs to predict NPC movement during zone owner transitions.
        /// </summary>
        public Vec3 GenerateChasePosition(short entityId, Vec3 targetPosition, float speed, float deltaTime)
        {
            var entity = _world.GetEntity(entityId);
            if (entity == null) return targetPosition;

            var direction = Vec3.Normalize(Vec3.Subtract(targetPosition, entity.Position));
            var moveDistance = speed * deltaTime;
            var distToTarget = Vec3.Distance(entity.Position, targetPosition);

            if (moveDistance >= distToTarget)
                return targetPosition;

            return Vec3.Add(entity.Position, Vec3.Scale(direction, moveDistance));
        }

        /// <summary>
        /// Generates a return-to-spawn movement position.
        /// </summary>
        public Vec3 GenerateReturnPosition(short entityId, float speed, float deltaTime)
        {
            var entity = _world.GetEntity(entityId);
            if (entity == null) return Vec3.Zero;

            return GenerateChasePosition(entityId, entity.SpawnPosition, speed, deltaTime);
        }

        /// <summary>
        /// Generates a wander position (random point near spawn).
        /// </summary>
        public Vec3 GenerateWanderPosition(short entityId, float wanderRadius)
        {
            var entity = _world.GetEntity(entityId);
            if (entity == null) return Vec3.Zero;

            var rng = new Random();
            var angle = (float)(rng.NextDouble() * Math.PI * 2);
            var dist = (float)(rng.NextDouble() * wanderRadius);

            return new Vec3(
                entity.SpawnPosition.X + (float)Math.Cos(angle) * dist,
                entity.SpawnPosition.Y,
                entity.SpawnPosition.Z + (float)Math.Sin(angle) * dist
            );
        }

        /// <summary>
        /// Removes movement tracking for an entity.
        /// </summary>
        public void RemoveEntity(short entityId)
        {
            _states.TryRemove(entityId, out _);
        }

        /// <summary>
        /// Clears all movement state for a zone.
        /// </summary>
        public void ClearZone(string zone)
        {
            if (string.IsNullOrEmpty(zone)) return;

            foreach (var kvp in _states)
            {
                var entity = _world.GetEntity(kvp.Key);
                if (entity != null && entity.Zone == zone)
                {
                    _states.TryRemove(kvp.Key, out _);
                }
            }
        }

        /// <summary>
        /// Gets the current speed of an entity.
        /// </summary>
        public float GetSpeed(short entityId)
        {
            return _states.TryGetValue(entityId, out var state) ? state.CurrentSpeed : 0;
        }

        public int GetTrackedCount() => _states.Count;
    }

    public enum MovementType : byte
    {
        Idle,
        Walk,
        Run,
        Chase,
        Flee,
        Return,
        Teleport
    }

    public class MovementState
    {
        public Vec3 PreviousPosition { get; set; }
        public Vec3 CurrentPosition { get; set; }
        public float CurrentSpeed { get; set; }
        public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
        public MovementType Type { get; set; } = MovementType.Idle;
    }

    public class MovementValidation
    {
        public bool IsValid { get; set; }
        public bool IsTeleport { get; set; }
        public string Reason { get; set; }
        public float Distance { get; set; }
        public float Speed { get; set; }
    }
}

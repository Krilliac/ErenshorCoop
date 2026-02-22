using System;
using System.Collections.Generic;
using ErenshorDedicatedServer.Data;
using LiteNetLib;

namespace ErenshorDedicatedServer.Network
{
    /// <summary>
    /// Represents a connected player session on the server.
    /// Tracks all per-player state including position, stats, rate limiting, etc.
    /// </summary>
    public class PlayerSession
    {
        public short PlayerId { get; set; } = -1;
        public NetPeer Peer { get; }
        public string CharacterName { get; set; } = "";
        public string Zone { get; set; } = "";
        public string PreviousZone { get; set; } = "";
        public PlayerClass Class { get; set; } = PlayerClass.Unknown;
        public int Level { get; set; } = 1;
        public int Health { get; set; } = 100;
        public int MaxHealth { get; set; } = 100;
        public int Mana { get; set; } = 100;
        public int MaxMana { get; set; } = 100;
        public bool IsAlive { get; set; } = true;
        public Vec3 Position { get; set; } = Vec3.Zero;
        public Quat Rotation { get; set; } = Quat.Identity;

        // Auth / Identity
        public ulong SteamId { get; set; }
        public bool IsAuthenticated { get; set; }
        public bool IsModerator { get; set; }
        public bool IsAdmin { get; set; }

        // Connection state
        public DateTime ConnectedAt { get; }
        public DateTime LastPacketTime { get; set; }
        public DateTime LastPositionUpdate { get; set; }
        public DateTime LastChatMessage { get; set; }
        public bool HasSentConnect { get; set; }

        // Look data (cached for relay to new connections)
        public bool IsMale { get; set; }
        public string HairName { get; set; } = "";
        public float HairColorR { get; set; }
        public float HairColorG { get; set; }
        public float HairColorB { get; set; }
        public float SkinColorR { get; set; }
        public float SkinColorG { get; set; }
        public float SkinColorB { get; set; }

        // Gear data (cached)
        public List<GearEntry> Gear { get; set; } = new List<GearEntry>();

        // Targeting
        public short TargetId { get; set; } = -1;
        public EntityType TargetType { get; set; }

        // Stats
        public StatBlock Stats { get; set; } = new StatBlock();

        // Sim companions (entityIDs of player's sims)
        public List<short> SimIds { get; set; } = new List<short>();

        // Pet
        public short PetEntityId { get; set; } = -1;

        // Group
        public int GroupId { get; set; } = -1;

        // Rate limiting
        private int _packetCount;
        private DateTime _packetCountReset;
        private int _chatCount;
        private DateTime _chatCountReset;

        public PlayerSession(NetPeer peer)
        {
            Peer = peer ?? throw new ArgumentNullException(nameof(peer));
            ConnectedAt = DateTime.UtcNow;
            LastPacketTime = DateTime.UtcNow;
            LastPositionUpdate = DateTime.UtcNow;
            LastChatMessage = DateTime.MinValue;
            _packetCountReset = DateTime.UtcNow;
            _chatCountReset = DateTime.UtcNow;
        }

        /// <summary>
        /// Checks packet rate limiting. Returns false if the player is sending too fast.
        /// </summary>
        public bool CheckPacketRate(int maxPerSecond)
        {
            var now = DateTime.UtcNow;
            if ((now - _packetCountReset).TotalSeconds >= 1.0)
            {
                _packetCount = 0;
                _packetCountReset = now;
            }

            _packetCount++;
            LastPacketTime = now;
            return _packetCount <= maxPerSecond;
        }

        /// <summary>
        /// Checks chat rate limiting. Returns false if the player is chatting too fast.
        /// </summary>
        public bool CheckChatRate(int maxPerSecond)
        {
            var now = DateTime.UtcNow;
            if ((now - _chatCountReset).TotalSeconds >= 1.0)
            {
                _chatCount = 0;
                _chatCountReset = now;
            }

            _chatCount++;
            LastChatMessage = now;
            return _chatCount <= maxPerSecond;
        }

        /// <summary>
        /// Validates a movement update. Returns false if the movement seems impossible.
        /// </summary>
        public bool ValidateMovement(Vec3 newPosition, float maxSpeed, float deltaTime)
        {
            if (deltaTime <= 0) return true; // Skip first frame

            var dist = Vec3.Distance(Position, newPosition);
            var maxDist = maxSpeed * deltaTime * 1.5f; // 1.5x tolerance

            // Allow some teleportation tolerance for zone changes
            if (dist > 1000f) return true; // Probably a zone change

            return dist <= maxDist + 2f; // 2 unit grace for network jitter
        }

        /// <summary>
        /// Checks if this player has timed out based on last packet time.
        /// </summary>
        public bool HasTimedOut(int timeoutSeconds)
        {
            return (DateTime.UtcNow - LastPacketTime).TotalSeconds > timeoutSeconds;
        }

        public override string ToString()
        {
            return $"[{PlayerId}] {CharacterName} ({Class}) Lv{Level} @{Zone}";
        }
    }

    public class GearEntry
    {
        public byte SlotType { get; set; }
        public string ItemId { get; set; } = "";
        public byte Quality { get; set; }
    }

    public class StatBlock
    {
        public int Strength { get; set; }
        public int Dexterity { get; set; }
        public int Intelligence { get; set; }
        public int Wisdom { get; set; }
        public int Agility { get; set; }
        public int Endurance { get; set; }
        public int Charisma { get; set; }
    }
}

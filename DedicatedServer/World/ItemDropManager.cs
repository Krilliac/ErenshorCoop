using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;
using ErenshorDedicatedServer.Network;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ErenshorDedicatedServer.World
{
    /// <summary>
    /// Manages item drops in the world. Tracks dropped items per zone,
    /// handles pickup/destroy, and relays drop events to zone players.
    /// </summary>
    public class ItemDropManager
    {
        private readonly NetworkManager _network;
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DroppedItem>> _droppedItems = new();

        public ItemDropManager(NetworkManager network)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
        }

        public void HandleItemDrop(PlayerSession session, NetDataReader reader)
        {
            if (session == null || reader == null) return;

            try
            {
                var senderId = reader.GetShort();
                var flag = reader.GetUShort();
                var dataTypes = PacketHelper.ReadSubTypeFlag<ItemDropType>(flag);

                string itemId = null;
                int quality = 0;
                Vec3 location = Vec3.Zero;
                string zone = "";
                string dropId = "";

                if (dataTypes.Contains(ItemDropType.DROP))
                {
                    itemId = PacketHelper.SafeReadString(reader, 128);
                    quality = reader.GetInt();
                    location = PacketHelper.ReadVec3(reader);
                    zone = PacketHelper.SafeReadString(reader, 64);
                    dropId = PacketHelper.SafeReadString(reader, 128);

                    // Validate
                    if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(dropId))
                    {
                        ServerLogger.Warning($"Invalid item drop from [{session.PlayerId}]", "ITEM");
                        return;
                    }

                    // Bounds check on quality
                    quality = Math.Clamp(quality, 0, 100);

                    // Track the drop
                    var zoneItems = _droppedItems.GetOrAdd(zone, _ => new ConcurrentDictionary<string, DroppedItem>());

                    // Limit drops per zone to prevent spam
                    if (zoneItems.Count >= 500)
                    {
                        ServerLogger.Warning($"Zone {zone} has too many drops, rejecting from [{session.PlayerId}]", "ITEM");
                        return;
                    }

                    zoneItems[dropId] = new DroppedItem
                    {
                        ItemId = itemId,
                        Quality = quality,
                        Location = location,
                        Zone = zone,
                        DropId = dropId,
                        DroppedBy = session.PlayerId,
                        DroppedAt = DateTime.UtcNow
                    };

                    ServerLogger.Debug($"Item dropped: {itemId} q{quality} in {zone} by [{session.PlayerId}]", "ITEM");
                }

                if (dataTypes.Contains(ItemDropType.DESTROY))
                {
                    dropId = PacketHelper.SafeReadString(reader, 128);
                    zone = session.Zone;

                    if (_droppedItems.TryGetValue(zone, out var zoneItems))
                    {
                        zoneItems.TryRemove(dropId, out _);
                    }

                    ServerLogger.Debug($"Item destroyed: {dropId} in {zone}", "ITEM");
                }

                if (dataTypes.Contains(ItemDropType.NEW_QUANTITY))
                {
                    dropId = PacketHelper.SafeReadString(reader, 128);
                    quality = reader.GetInt();
                    zone = session.Zone;

                    if (_droppedItems.TryGetValue(zone, out var zoneItems))
                    {
                        if (zoneItems.TryGetValue(dropId, out var item))
                        {
                            item.Quality = quality;
                        }
                    }
                }

                // Relay to zone players
                RelayItemDropToZone(session, reader);
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Error handling item drop from [{session.PlayerId}]: {ex.Message}", "ITEM");
            }
        }

        private void RelayItemDropToZone(PlayerSession session, NetDataReader reader)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.ITEM_DROP);
            writer.Put(reader.RawData, 1, reader.RawDataSize - 1);

            _network.BroadcastToZone(session.Zone, writer, DeliveryMethod.ReliableOrdered,
                PacketHelper.GetChannel(PacketType.ITEM_DROP), session.PlayerId);
        }

        /// <summary>
        /// Send all existing dropped items in a zone to a player.
        /// </summary>
        public void SendZoneDropsToPlayer(PlayerSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.Zone)) return;

            if (!_droppedItems.TryGetValue(session.Zone, out var zoneItems)) return;

            foreach (var kvp in zoneItems)
            {
                var item = kvp.Value;
                var writer = new NetDataWriter();
                writer.Put((byte)PacketType.ITEM_DROP);
                writer.Put((short)0); // sender
                var flags = new HashSet<ItemDropType> { ItemDropType.DROP };
                writer.Put(PacketHelper.GetSubTypeFlag(flags));
                writer.Put(item.ItemId);
                writer.Put(item.Quality);
                PacketHelper.PutVec3(writer, item.Location);
                writer.Put(item.Zone);
                writer.Put(item.DropId);

                _network.SendTo(session, writer, DeliveryMethod.ReliableOrdered,
                    PacketHelper.GetChannel(PacketType.ITEM_DROP));
            }
        }

        /// <summary>
        /// Periodic cleanup of old drops.
        /// </summary>
        public void Tick(float deltaTime)
        {
            var now = DateTime.UtcNow;
            foreach (var zoneKvp in _droppedItems)
            {
                foreach (var itemKvp in zoneKvp.Value)
                {
                    // Remove drops older than 10 minutes
                    if ((now - itemKvp.Value.DroppedAt).TotalMinutes > 10)
                    {
                        zoneKvp.Value.TryRemove(itemKvp.Key, out _);
                    }
                }
            }
        }

        public int GetTotalDropCount()
        {
            return _droppedItems.Values.Sum(z => z.Count);
        }
    }

    public class DroppedItem
    {
        public string ItemId { get; set; }
        public int Quality { get; set; }
        public Vec3 Location { get; set; }
        public string Zone { get; set; }
        public string DropId { get; set; }
        public short DroppedBy { get; set; }
        public DateTime DroppedAt { get; set; }
    }
}

using System;
using System.Collections.Generic;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;
using ErenshorDedicatedServer.Network;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ErenshorDedicatedServer.World
{
    /// <summary>
    /// Manages weather synchronization across clients. Relays weather data
    /// from zone owners and handles weather state when zone ownership transfers.
    /// </summary>
    public class WeatherManager
    {
        private readonly NetworkManager _network;
        private readonly Dictionary<string, byte[]> _zoneWeatherData = new();
        private readonly object _weatherLock = new object();

        public WeatherManager(NetworkManager network)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
        }

        /// <summary>
        /// Handle weather data from a zone owner. Cache it and relay.
        /// </summary>
        public void HandleWeatherData(PlayerSession session, NetDataReader reader)
        {
            if (session == null || reader == null) return;

            try
            {
                // Cache raw weather data for the zone
                var rawData = new byte[reader.RawDataSize];
                Buffer.BlockCopy(reader.RawData, 0, rawData, 0, reader.RawDataSize);

                lock (_weatherLock)
                {
                    _zoneWeatherData[session.Zone] = rawData;
                }

                // Relay to zone players (the packet already contains target player IDs)
                var writer = new NetDataWriter();
                writer.Put((byte)PacketType.WEATHER_DATA);
                writer.Put(reader.RawData, 1, reader.RawDataSize - 1);

                _network.BroadcastToZone(session.Zone, writer, DeliveryMethod.ReliableOrdered,
                    PacketHelper.GetChannel(PacketType.WEATHER_DATA), session.PlayerId);
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Error handling weather data: {ex.Message}", "WEATHER");
            }
        }

        /// <summary>
        /// Send cached weather data for a zone to a specific player.
        /// </summary>
        public void SendWeatherToPlayer(PlayerSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.Zone)) return;

            byte[] data;
            lock (_weatherLock)
            {
                if (!_zoneWeatherData.TryGetValue(session.Zone, out data))
                    return;
            }

            if (data == null || data.Length == 0) return;

            try
            {
                var writer = new NetDataWriter();
                writer.Put(data);
                _network.SendTo(session, writer, DeliveryMethod.ReliableOrdered,
                    PacketHelper.GetChannel(PacketType.WEATHER_DATA));
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Error sending weather to [{session.PlayerId}]: {ex.Message}", "WEATHER");
            }
        }
    }
}

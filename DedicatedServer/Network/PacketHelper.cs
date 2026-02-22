using System;
using System.Collections.Generic;
using ErenshorDedicatedServer.Data;
using LiteNetLib.Utils;

namespace ErenshorDedicatedServer.Network
{
    /// <summary>
    /// Utility methods for reading and writing packets compatible with the ErenshorCoop client protocol.
    /// Mirrors the Shared/Extensions.cs serialization format.
    /// </summary>
    public static class PacketHelper
    {
        // ---------- Writers ----------

        public static void PutVec3(NetDataWriter writer, Vec3 v)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.Put(v.X);
            writer.Put(v.Y);
            writer.Put(v.Z);
        }

        public static void PutQuat(NetDataWriter writer, Quat q)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.Put(q.X);
            writer.Put(q.Y);
            writer.Put(q.Z);
            writer.Put(q.W);
        }

        public static void PutColor(NetDataWriter writer, float r, float g, float b)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.Put(r);
            writer.Put(g);
            writer.Put(b);
        }

        // ---------- Readers ----------

        public static Vec3 ReadVec3(NetDataReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            return new Vec3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        }

        public static Quat ReadQuat(NetDataReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            return new Quat(reader.GetFloat(), reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
        }

        // ---------- SubType Flags ----------

        public static ushort GetSubTypeFlag<T>(HashSet<T> dataTypes) where T : Enum
        {
            if (dataTypes == null) return 0;

            ushort val = 1;
            ushort flag = 0;
            foreach (T enumVal in Enum.GetValues(typeof(T)))
            {
                if (dataTypes.Contains(enumVal))
                    flag |= val;
                val *= 2;
            }
            return flag;
        }

        public static HashSet<T> ReadSubTypeFlag<T>(ushort flag) where T : Enum
        {
            var dataTypes = new HashSet<T>();
            ushort val = 1;
            foreach (T enumVal in Enum.GetValues(typeof(T)))
            {
                if ((flag & val) != 0)
                    dataTypes.Add(enumVal);
                val *= 2;
            }
            return dataTypes;
        }

        // ---------- String Sanitization ----------

        public static string Sanitize(string input, int maxLength = 256)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            // Escape angle brackets (rich text injection prevention)
            var clean = input.Replace("<", "&lt;").Replace(">", "&gt;");

            // Remove control characters
            var chars = new char[clean.Length];
            var idx = 0;
            foreach (var c in clean)
            {
                if (!char.IsControl(c))
                    chars[idx++] = c;
            }

            var result = new string(chars, 0, idx);
            return result.Length > maxLength ? result.Substring(0, maxLength) : result;
        }

        public static string SafeReadString(NetDataReader reader, int maxLength = 256)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            return Sanitize(reader.GetString(), maxLength);
        }

        // ---------- Channel Mapping ----------

        /// <summary>
        /// Returns the channel number for a given packet type.
        /// Mirrors the client GenericPacketModule registration.
        /// </summary>
        public static byte GetChannel(PacketType type)
        {
            return type switch
            {
                PacketType.PLAYER_TRANSFORM => 2,
                PacketType.PLAYER_CONNECT => 3,
                PacketType.PLAYER_DATA => 3,
                PacketType.PLAYER_ACTION => 3,
                PacketType.SERVER_CONNECT => 3,
                PacketType.ENTITY_TRANSFORM => 4,
                PacketType.ENTITY_SPAWN => 5,
                PacketType.ENTITY_ACTION => 5,
                PacketType.ENTITY_DATA => 5,
                PacketType.DISCONNECT => 0,
                PacketType.SERVER_INFO => 0,
                PacketType.SERVER_REQUEST => 0,
                PacketType.PLAYER_MESSAGE => 8,
                PacketType.PLAYER_REQUEST => 8,
                PacketType.ITEM_DROP => 8,
                PacketType.WEATHER_DATA => 8,
                PacketType.GROUP => 9,
                PacketType.SERVER_GROUP => 9,
                _ => 2
            };
        }

        /// <summary>
        /// Returns the delivery method for a given packet type.
        /// Transform packets use Unreliable; most others use ReliableOrdered.
        /// </summary>
        public static LiteNetLib.DeliveryMethod GetDeliveryMethod(PacketType type)
        {
            return type switch
            {
                PacketType.PLAYER_TRANSFORM => LiteNetLib.DeliveryMethod.Unreliable,
                PacketType.ENTITY_TRANSFORM => LiteNetLib.DeliveryMethod.Unreliable,
                _ => LiteNetLib.DeliveryMethod.ReliableOrdered
            };
        }
    }
}

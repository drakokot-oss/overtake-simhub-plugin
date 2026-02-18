using System;

namespace Overtake.SimHub.Plugin.Packets
{
    /// <summary>
    /// F1 25 UDP packet header — 29 bytes at the start of every packet.
    /// </summary>
    public class PacketHeader
    {
        public const int Size = 29;

        public ushort PacketFormat;
        public byte GameYear;
        public byte GameMajorVersion;
        public byte GameMinorVersion;
        public byte PacketVersion;
        public byte PacketId;
        public ulong SessionUid;
        public float SessionTime;
        public uint FrameIdentifier;
        public uint OverallFrameIdentifier;
        public byte PlayerCarIndex;
        public byte SecondaryPlayerCarIndex;

        public static PacketHeader Parse(byte[] data)
        {
            if (data == null || data.Length < Size)
                return null;

            return new PacketHeader
            {
                PacketFormat = BitConverter.ToUInt16(data, 0),
                GameYear = data[2],
                GameMajorVersion = data[3],
                GameMinorVersion = data[4],
                PacketVersion = data[5],
                PacketId = data[6],
                SessionUid = BitConverter.ToUInt64(data, 7),
                SessionTime = BitConverter.ToSingle(data, 15),
                FrameIdentifier = BitConverter.ToUInt32(data, 19),
                OverallFrameIdentifier = BitConverter.ToUInt32(data, 23),
                PlayerCarIndex = data[27],
                SecondaryPlayerCarIndex = data[28],
            };
        }
    }
}

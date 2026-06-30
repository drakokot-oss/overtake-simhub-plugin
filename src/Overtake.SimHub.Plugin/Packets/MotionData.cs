using System;

namespace Overtake.SimHub.Plugin.Packets
{
    /// <summary>
    /// Packet ID 0: Motion — one CarMotionData entry per car. We only read what the
    /// live Track Map needs: world X/Z position and yaw.
    ///
    /// FORMAT-AWARE (v1 wrongly assumed the layout was stable). The 2026 wire QUANTISES
    /// the gForce fields from float[3] to int16[3], shrinking each entry from 60 → 54
    /// bytes and moving yaw from @48 to @42. Confirmed by the official EA 2026 spec size:
    /// PacketMotionData = 1325 bytes = 29 (header) + 24 * 54. Using the wrong stride
    /// drifts every car index > 0, producing garbage coordinates (the broken track map).
    /// worldPositionX@0 / worldPositionZ@8 are unchanged (they precede gForce).
    ///
    /// Live race UI only (Track Map). Not used by the .otk export pipeline.
    /// Per-car: posX@0 posY@4 posZ@8 ; vel@12 ; fwdDir(3xs16)@24 ; rightDir(3xs16)@30 ;
    ///   2025: gForce(3xfloat)@36, yaw@48, pitch@52, roll@56  -> entry 60
    ///   2026: gForce(3xs16)@36,   yaw@42, pitch@46, roll@50  -> entry 54
    /// </summary>
    public class MotionEntry
    {
        public const int EntrySize2025 = 60;
        public const int EntrySize2026 = 54;
        public const int NumCars = GameInfo.MaxSupportedCars;

        public int CarIdx;
        public float WorldX;
        public float WorldZ;
        public float Yaw;

        /// <summary>Backwards-compatible entry point — assumes the 2025 wire format.</summary>
        public static MotionEntry[] Parse(byte[] data)
        {
            return Parse(data, 2025, null);
        }

        public static MotionEntry[] Parse(byte[] data, ushort packetFormat)
        {
            return Parse(data, packetFormat, null);
        }

        public static MotionEntry[] Parse(byte[] data, ushort packetFormat, ushort? bodyWireFormatOverride)
        {
            if (data == null || data.Length < PacketHeader.Size + 12)
                return null;

            ushort bodyFmt = packetFormat >= 2026
                ? (bodyWireFormatOverride ?? (ushort)2026)
                : (ushort)2025;
            int entrySize = bodyFmt >= 2026 ? EntrySize2026 : EntrySize2025;
            int yawOff = bodyFmt >= 2026 ? 42 : 48;

            var entries = new MotionEntry[NumCars];
            int p = PacketHeader.Size;

            for (int i = 0; i < NumCars; i++)
            {
                int off = p + i * entrySize;
                if (off + yawOff + 4 > data.Length)
                    break;

                entries[i] = new MotionEntry
                {
                    CarIdx = i,
                    WorldX = BitConverter.ToSingle(data, off + 0),
                    WorldZ = BitConverter.ToSingle(data, off + 8),
                    Yaw = BitConverter.ToSingle(data, off + yawOff),
                };
            }

            return entries;
        }
    }
}

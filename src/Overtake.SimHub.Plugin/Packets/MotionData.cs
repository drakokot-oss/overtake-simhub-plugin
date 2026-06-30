using System;

namespace Overtake.SimHub.Plugin.Packets
{
    /// <summary>
    /// Packet ID 0: Motion — one CarMotionData entry per car, 60 bytes each.
    /// We only read what the live Track Map needs: world X/Z position and yaw.
    /// The world position block sits at the very start of each entry, so the
    /// layout is stable across 2025/2026 (the appended fields don't shift it).
    ///
    /// Live race UI only (Track Map). Not used by the .otk export pipeline.
    /// Layout per car (packed, little-endian):
    ///   f(4) worldPositionX   @ 0
    ///   f(4) worldPositionY   @ 4
    ///   f(4) worldPositionZ   @ 8
    ///   f(4)*3 worldVelocity  @ 12
    ///   s16*6 fwd/right dirs  @ 24
    ///   f(4)*3 gForce         @ 36
    ///   f(4) yaw              @ 48
    ///   f(4) pitch            @ 52
    ///   f(4) roll             @ 56
    /// </summary>
    public class MotionEntry
    {
        public const int EntrySize = 60;
        public const int NumCars = GameInfo.MaxSupportedCars;

        public int CarIdx;
        public float WorldX;
        public float WorldZ;
        public float Yaw;

        public static MotionEntry[] Parse(byte[] data)
        {
            if (data == null || data.Length < PacketHeader.Size + EntrySize)
                return null;

            var entries = new MotionEntry[NumCars];
            int p = PacketHeader.Size;

            for (int i = 0; i < NumCars; i++)
            {
                int off = p + i * EntrySize;
                if (off + EntrySize > data.Length)
                    break;

                entries[i] = new MotionEntry
                {
                    CarIdx = i,
                    WorldX = BitConverter.ToSingle(data, off + 0),
                    WorldZ = BitConverter.ToSingle(data, off + 8),
                    Yaw = BitConverter.ToSingle(data, off + 48),
                };
            }

            return entries;
        }
    }
}

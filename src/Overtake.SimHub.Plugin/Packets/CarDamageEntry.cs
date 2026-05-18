using System;

namespace Overtake.SimHub.Plugin.Packets
{
    public class TyreSet
    {
        public float RL;
        public float RR;
        public float FL;
        public float FR;
    }

    public class WingDamage
    {
        public byte FrontLeft;
        public byte FrontRight;
        public byte Rear;
    }

    /// <summary>
    /// Packet ID 10: Car Damage — 46 bytes per car.
    /// F1 25: 22 cars. F1 26 (mod): up to 24. The parser reads as many
    /// entries as the buffer can hold (capped at <see cref="GameInfo.MaxSupportedCars"/>).
    /// Layout: 4f(tyreWear) + 4B(tyresDamage) + 4B(brakesDamage) + 4B(tyreBlisters) + 18B(wing/engine)
    /// </summary>
    public class CarDamageEntry
    {
        public const int EntrySize = 46;
        /// <summary>Parser cap. Tracks <see cref="GameInfo.MaxSupportedCars"/>.</summary>
        public const int NumCars = GameInfo.MaxSupportedCars;

        public int CarIdx;
        public TyreSet TyreWear;
        public float TyreWearAvg;
        public TyreSet TyresDamage;
        public WingDamage Wing;

        public static CarDamageEntry[] Parse(byte[] data)
        {
            // Need at least the header plus one entry to do anything useful.
            if (data == null || data.Length < PacketHeader.Size + EntrySize)
                return null;

            var entries = new CarDamageEntry[NumCars];
            int p = PacketHeader.Size;

            for (int i = 0; i < NumCars; i++)
            {
                int off = p + i * EntrySize;
                // Bail when the buffer runs short. Trailing slots stay null.
                if (off + EntrySize > data.Length)
                    break;

                float wRL = BitConverter.ToSingle(data, off + 0);
                float wRR = BitConverter.ToSingle(data, off + 4);
                float wFL = BitConverter.ToSingle(data, off + 8);
                float wFR = BitConverter.ToSingle(data, off + 12);

                entries[i] = new CarDamageEntry
                {
                    CarIdx = i,
                    TyreWear = new TyreSet { RL = wRL, RR = wRR, FL = wFL, FR = wFR },
                    TyreWearAvg = (float)Math.Round((wRL + wRR + wFL + wFR) / 4.0, 1),
                    TyresDamage = new TyreSet
                    {
                        RL = data[off + 16],
                        RR = data[off + 17],
                        FL = data[off + 18],
                        FR = data[off + 19],
                    },
                    Wing = new WingDamage
                    {
                        // After 4f(16) + 4B(4) + 4B(4) + 4B(4) = offset 28
                        FrontLeft = data[off + 28],
                        FrontRight = data[off + 29],
                        Rear = data[off + 30],
                    },
                };
            }

            return entries;
        }
    }
}

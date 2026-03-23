using System;

namespace Overtake.SimHub.Plugin.Packets
{
    public class CarStatusEntry
    {
        public byte TractionControl;
        public byte AntiLockBrakes;
        public byte FuelMix;
        public float FuelInTank;
        public float FuelCapacity;
        public float FuelRemainingLaps;

        private const int EntrySize = 55;

        public static CarStatusEntry[] Parse(byte[] data)
        {
            if (data == null || data.Length < PacketHeader.Size + EntrySize)
                return null;

            int p = PacketHeader.Size;
            int maxCars = Math.Min(22, (data.Length - p) / EntrySize);
            var entries = new CarStatusEntry[maxCars];

            for (int i = 0; i < maxCars; i++)
            {
                int off = p + i * EntrySize;
                if (off + 17 > data.Length) break;
                entries[i] = new CarStatusEntry
                {
                    TractionControl = data[off + 0],
                    AntiLockBrakes = data[off + 1],
                    FuelMix = data[off + 2],
                    FuelInTank = BitConverter.ToSingle(data, off + 5),
                    FuelCapacity = BitConverter.ToSingle(data, off + 9),
                    FuelRemainingLaps = BitConverter.ToSingle(data, off + 13),
                };
            }

            return entries;
        }
    }
}

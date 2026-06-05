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

        // v1.1.34 — ERS fields. All energies are in Joules as delivered by F1 25
        // UDP. Conversion to MJ/percent is done in SessionStore at sampling time
        // so the rest of the pipeline never sees raw Joules.
        public float EnginePowerIce;          // Watts (off 29)
        public float EnginePowerMguk;         // Watts (off 33)
        public float ErsStoreEnergy;          // Joules, 0..ErsMaxJoules (off 37)
        public byte ErsDeployMode;            // 0=None,1=Medium,2=HotLap,3=Overtake (off 41)
        public float ErsHarvestedThisLapMguk; // Joules, reset at lap rollover (off 42)
        public float ErsHarvestedThisLapMguh; // Joules, reset at lap rollover (off 46)
        public float ErsDeployedThisLap;      // Joules, reset at lap rollover (off 50)
        public byte NetworkPaused;            // 1 when this car is paused on the network (off 54)
        public bool ErsCaptured;              // true when bytes 29..54 were actually parsed

        private const int EntrySize2025 = 55;
        // v1.1.40 — F1 26 / 2026 Season Pack grows each CarStatus entry from 55 to
        // 59 bytes (+4 trailing, likely Active Aero / Overtake / Boost state). The
        // ERS / fuel field offsets WITHIN the entry are unchanged, so only the
        // stride differs. Confirmed via labeled 2026 capture — see
        // docs/F1-26-UDP-OFFSET-MAP.md.
        private const int EntrySize2026 = 59;
        private const int FuelOnlyMinSize = 17;
        private const int FullEntryMinSize = 55;

        private static int EntrySizeFor(ushort packetFormat)
        {
            return packetFormat >= 2026 ? EntrySize2026 : EntrySize2025;
        }

        /// <summary>Backwards-compatible entry point — assumes the 2025 wire format.</summary>
        public static CarStatusEntry[] Parse(byte[] data)
        {
            return Parse(data, 2025);
        }

        /// <summary>
        /// Parses a CarStatus packet using the entry stride for the given UDP wire
        /// format. v1.1.40 — added 2026 stride (59) support; ERS offsets unchanged.
        /// </summary>
        public static CarStatusEntry[] Parse(byte[] data, ushort packetFormat)
        {
            if (data == null || data.Length < PacketHeader.Size + FuelOnlyMinSize)
                return null;

            int p = PacketHeader.Size;
            int entrySize = EntrySizeFor(packetFormat);
            // F1 25 sends 22 entries; F1 26 (mod) sends up to 24.
            // Cap at GameInfo.MaxSupportedCars so a hypothetical larger grid in
            // future patches is not silently truncated.
            int maxCars = Math.Min(GameInfo.MaxSupportedCars, (data.Length - p) / entrySize);
            var entries = new CarStatusEntry[maxCars];

            for (int i = 0; i < maxCars; i++)
            {
                int off = p + i * entrySize;
                if (off + FuelOnlyMinSize > data.Length) break;
                var entry = new CarStatusEntry
                {
                    TractionControl = data[off + 0],
                    AntiLockBrakes = data[off + 1],
                    FuelMix = data[off + 2],
                    FuelInTank = BitConverter.ToSingle(data, off + 5),
                    FuelCapacity = BitConverter.ToSingle(data, off + 9),
                    FuelRemainingLaps = BitConverter.ToSingle(data, off + 13),
                };

                // ERS section is optional: F1 24 and earlier may send a shorter entry.
                // We still want fuel/TC/ABS in that case, so we degrade gracefully.
                if (off + FullEntryMinSize <= data.Length)
                {
                    entry.EnginePowerIce = BitConverter.ToSingle(data, off + 29);
                    entry.EnginePowerMguk = BitConverter.ToSingle(data, off + 33);
                    entry.ErsStoreEnergy = BitConverter.ToSingle(data, off + 37);
                    entry.ErsDeployMode = data[off + 41];
                    entry.ErsHarvestedThisLapMguk = BitConverter.ToSingle(data, off + 42);
                    entry.ErsHarvestedThisLapMguh = BitConverter.ToSingle(data, off + 46);
                    entry.ErsDeployedThisLap = BitConverter.ToSingle(data, off + 50);
                    entry.NetworkPaused = data[off + 54];
                    entry.ErsCaptured = true;
                }

                entries[i] = entry;
            }

            return entries;
        }
    }
}

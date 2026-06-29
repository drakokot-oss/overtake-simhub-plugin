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

        // Live race UI (v1 broadcast): tyre compound + age. Offsets 25/26/27 are
        // identical for the 2025 and 2026 strides (the 2026 insert is at off 50).
        public byte ActualTyreCompound;
        public byte VisualTyreCompound;
        public byte TyresAgeLaps;

        // v1.1.34 — ERS fields. All energies are in Joules as delivered by F1 25
        // UDP. Conversion to MJ/percent is done in SessionStore at sampling time
        // so the rest of the pipeline never sees raw Joules.
        public float EnginePowerIce;          // Watts (off 29)
        public float EnginePowerMguk;         // Watts (off 33)
        public float ErsStoreEnergy;          // Joules, 0..ErsMaxJoules (off 37)
        public byte ErsDeployMode;            // 0=None,1=Medium,2=HotLap,3=Overtake (off 41)
        public float ErsHarvestedThisLapMguk; // Joules, reset at lap rollover (off 42)
        public float ErsHarvestedThisLapMguh; // Joules, reset at lap rollover (off 46)
        public float ErsHarvestedLimitPerLap; // Joules; 2026 only, new field at off 50
        public float ErsDeployedThisLap;      // Joules, reset at lap rollover (2025 off 50 / 2026 off 54)
        public byte NetworkPaused;            // 1 when this car is paused on the network (2025 off 54 / 2026 off 58)
        public bool ErsCaptured;              // true when the ERS block was actually parsed

        private const int EntrySize2025 = 55;
        // v1.1.40 / v1.1.47 — F1 26 / 2026 Season Pack grows each CarStatus entry from
        // 55 to 59 bytes. The +4 is NOT trailing: the official 2026 spec inserts a new
        // float m_ersHarvestedLimitPerLap at offset 50, which shifts m_ersDeployedThisLap
        // from 50->54 and m_networkPaused from 54->58. The ERS tail is therefore read
        // format-aware in Parse(). (Pre-v1.1.47 used the 2025 offsets for both formats,
        // so on F1 26 "deployed" was actually the harvest LIMIT — the spurious ">100%
        // deploy" that v1.1.42 wrongly rationalized as a new energy model — and a byte of
        // the deployed float was misread as networkPaused, dropping valid ERS samples.)
        private const int EntrySize2026 = 59;
        private const int FuelOnlyMinSize = 17;
        private const int FullEntryMinSize = 55;

        private static int EntrySizeForBodyWireFormat(ushort bodyWireFormat)
        {
            return bodyWireFormat >= 2026 ? EntrySize2026 : EntrySize2025;
        }

        internal static ushort ProbeBodyWireFormat(byte[] data)
        {
            int score2025 = ScoreStride(data, EntrySize2025);
            int score2026 = ScoreStride(data, EntrySize2026);
            return score2025 > score2026 ? (ushort)2025 : (ushort)2026;
        }

        private static int ScoreStride(byte[] data, int entrySize)
        {
            if (data == null || data.Length < PacketHeader.Size + FuelOnlyMinSize)
                return int.MinValue / 4;

            int p = PacketHeader.Size;
            int maxCars = Math.Min(4, (data.Length - p) / entrySize);
            if (maxCars < 1) return int.MinValue / 4;

            int score = 0;
            for (int i = 0; i < maxCars; i++)
            {
                int off = p + i * entrySize;
                if (off + FullEntryMinSize > data.Length) break;

                float fuelCap = BitConverter.ToSingle(data, off + 9);
                float ersStore = BitConverter.ToSingle(data, off + 37);
                byte deployMode = data[off + 41];

                if (fuelCap >= 80f && fuelCap <= 130f) score += 25;
                else if (float.IsNaN(fuelCap) || fuelCap > 500f || fuelCap < 1f) score -= 35;

                if (ersStore >= 0f && ersStore <= 5000000f) score += 20;
                else if (ersStore > 1e10f || float.IsNaN(ersStore)) score -= 45;

                if (deployMode <= 3) score += 5;
                else score -= 10;
            }
            return score;
        }

        /// <summary>Backwards-compatible entry point — assumes the 2025 wire format.</summary>
        public static CarStatusEntry[] Parse(byte[] data)
        {
            return Parse(data, 2025);
        }

        /// <summary>
        /// Parses a CarStatus packet using the entry stride for the given UDP wire
        /// format. v1.1.40 — added 2026 stride (59) support; ERS offsets unchanged.
        /// v1.1.46 — probe 2025 vs 2026 stride when header is 2026 (My Team online).
        /// </summary>
        public static CarStatusEntry[] Parse(byte[] data, ushort packetFormat)
        {
            return Parse(data, packetFormat, null);
        }

        public static CarStatusEntry[] Parse(byte[] data, ushort packetFormat, ushort? bodyWireFormatOverride)
        {
            if (data == null || data.Length < PacketHeader.Size + FuelOnlyMinSize)
                return null;

            int p = PacketHeader.Size;
            ushort bodyFmt = packetFormat >= 2026
                ? (bodyWireFormatOverride ?? ProbeBodyWireFormat(data))
                : (ushort)2025;
            int entrySize = EntrySizeForBodyWireFormat(bodyFmt);
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

                // Tyre compound + age (off 25/26/27) — same in 2025 and 2026 strides.
                if (off + 28 <= data.Length)
                {
                    entry.ActualTyreCompound = data[off + 25];
                    entry.VisualTyreCompound = data[off + 26];
                    entry.TyresAgeLaps = data[off + 27];
                }

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
                    if (bodyFmt >= 2026)
                    {
                        // 2026 inserts m_ersHarvestedLimitPerLap (float) at off 50,
                        // shifting deployed -> 54 and networkPaused -> 58.
                        if (off + EntrySize2026 <= data.Length)
                        {
                            entry.ErsHarvestedLimitPerLap = BitConverter.ToSingle(data, off + 50);
                            entry.ErsDeployedThisLap = BitConverter.ToSingle(data, off + 54);
                            entry.NetworkPaused = data[off + 58];
                            entry.ErsCaptured = true;
                        }
                    }
                    else
                    {
                        entry.ErsDeployedThisLap = BitConverter.ToSingle(data, off + 50);
                        entry.NetworkPaused = data[off + 54];
                        entry.ErsCaptured = true;
                    }
                }

                entries[i] = entry;
            }

            return entries;
        }
    }
}

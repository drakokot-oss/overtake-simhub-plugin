using System;

namespace Overtake.SimHub.Plugin.Packets
{
    public class FinalClassificationEntry
    {
        public int CarIdx;
        public byte Position;
        public byte NumLaps;
        public byte GridPosition;
        public byte Points;
        public byte NumPitStops;
        public byte ResultStatus;
        public byte ResultReason;
        public uint BestLapTimeMs;
        public double TotalRaceTimeSec;
        public byte PenaltiesTimeSec;
        public byte NumPenalties;
        public byte NumTyreStints;
        public byte[] TyreStintsActual;
        public byte[] TyreStintsVisual;
        public byte[] TyreStintsEndLaps;
    }

    /// <summary>
    /// Packet ID 8: Final Classification.
    /// F1 25 payload: 1 byte numCars, then 22 x 46 bytes (fixed array).
    /// F1 26 will likely carry up to 24 rows. Per-car layout: 7B + I(4) + d(8)
    /// + 3B + 8B*3 = 46 bytes. We parse every row the buffer can hold (capped
    /// at <see cref="GameInfo.MaxSupportedCars"/>) so no classified car is
    /// lost, even when the game's reported numCars undercounts (e.g. spectator
    /// mode bug seen on F1 25).
    /// </summary>
    public class FinalClassificationData
    {
        /// <summary>
        /// Kept for backwards compat with call sites that reference
        /// <c>FinalClassificationData.MaxCars</c>. Tracks the parser cap.
        /// </summary>
        public const int MaxCars = GameInfo.MaxSupportedCars;
        private const int RowSize = 46;

        public byte NumCars;
        public FinalClassificationEntry[] Classification;

        public static FinalClassificationData Parse(byte[] data)
        {
            if (data == null || data.Length < PacketHeader.Size + 1)
                return null;

            int p = PacketHeader.Size;
            byte numCars = data[p];
            int off = p + 1;

            var rows = new FinalClassificationEntry[MaxCars];

            for (int i = 0; i < MaxCars; i++)
            {
                if (off + RowSize > data.Length)
                    break;

                byte position = data[off + 0];
                byte numLaps = data[off + 1];
                byte gridPos = data[off + 2];
                byte points = data[off + 3];
                byte numPitStops = data[off + 4];
                byte resultStatus = data[off + 5];
                byte resultReason = data[off + 6];

                uint bestLapTime = BitConverter.ToUInt32(data, off + 7);
                double totalRaceTime = BitConverter.ToDouble(data, off + 11);

                byte penaltiesTime = data[off + 19];
                byte numPenalties = data[off + 20];
                byte numTyreStints = data[off + 21];

                var stintsActual = new byte[8];
                var stintsVisual = new byte[8];
                var stintsEndLaps = new byte[8];
                if (off + 22 + 24 <= data.Length)
                {
                    Array.Copy(data, off + 22, stintsActual, 0, 8);
                    Array.Copy(data, off + 30, stintsVisual, 0, 8);
                    Array.Copy(data, off + 38, stintsEndLaps, 0, 8);
                }

                rows[i] = new FinalClassificationEntry
                {
                    CarIdx = i,
                    Position = position,
                    NumLaps = numLaps,
                    GridPosition = gridPos,
                    Points = points,
                    NumPitStops = numPitStops,
                    ResultStatus = resultStatus,
                    ResultReason = resultReason,
                    BestLapTimeMs = bestLapTime,
                    TotalRaceTimeSec = totalRaceTime,
                    PenaltiesTimeSec = penaltiesTime,
                    NumPenalties = numPenalties,
                    NumTyreStints = numTyreStints,
                    TyreStintsActual = stintsActual,
                    TyreStintsVisual = stintsVisual,
                    TyreStintsEndLaps = stintsEndLaps,
                };

                off += RowSize;
            }

            return new FinalClassificationData
            {
                NumCars = numCars,
                Classification = rows,
            };
        }
    }
}

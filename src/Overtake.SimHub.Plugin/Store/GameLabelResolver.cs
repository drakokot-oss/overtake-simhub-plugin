using System.Collections.Generic;
using Overtake.SimHub.Plugin.Packets;

namespace Overtake.SimHub.Plugin.Store
{
    /// <summary>
    /// Resultado da detecção de jogo — o rótulo final + os sinais crus que o .otk
    /// expõe na metadata da captura.
    /// </summary>
    public sealed class GameLabelInfo
    {
        /// <summary>Rótulo final: "F1_26" / "F1_25" / "F1_&lt;ano&gt;" (conteúdo primeiro).</summary>
        public string GameLabel;
        /// <summary>Rótulo derivado SÓ do packet format (sem os sinais de conteúdo).</summary>
        public string FormatLabel;
        /// <summary>Sinais de conteúdo do "2026 Season Pack" presentes.</summary>
        public bool ContentPack2026;
        public ushort NewestPacketFormat;
        public byte NewestGameYear;
        public byte NewestGameMajor;
        public byte NewestGameMinor;
    }

    /// <summary>
    /// FONTE ÚNICA do rótulo de jogo (F1_25 / F1_26 / F1_&lt;ano&gt;). O .otk (LeagueFinalizer)
    /// e o snapshot ao vivo (LiveSnapshotBuilder) chamam este resolver para NUNCA
    /// divergirem em como o conteúdo F1 26 é detectado.
    ///
    /// O "2026 Season Pack" do F1 26 roda no wire format 2025, então o packetFormat
    /// sozinho reporta F1_25 mesmo com conteúdo 2026. Sinais de CONTEÚDO (sobrevivem a
    /// uma captura em formato 2025) vencem primeiro: team ids 220-230, Madring (track
    /// 42) ou grid &gt; 20 (F1 26 = 22 carros / 11 equipes; F1 25 vai até 20). Senão,
    /// confia no packet format mais novo (mapeia 2026 -&gt; F1_26 e formato futuro ->
    /// F1_&lt;ano&gt;), depois cai para um game year reportado &gt;= 26, depois F1_25.
    ///
    /// Consolida o que antes era duplicado em LiveSnapshotBuilder.GameLabel e no bloco
    /// inline do LeagueFinalizer (que tinham cadeias de fallback ligeiramente diferentes
    /// — risco de drift ao adicionar um novo sinal só em um lado).
    /// </summary>
    public static class GameLabelResolver
    {
        public static GameLabelInfo Resolve(IEnumerable<SessionRun> sessions)
        {
            var info = new GameLabelInfo();
            bool anyGameYear2026 = false;

            if (sessions != null)
            {
                foreach (var s in sessions)
                {
                    if (s == null) continue;

                    // Sinais de conteúdo 2026 (independentes do wire format).
                    if (s.TrackId.HasValue && s.TrackId.Value == GameInfo.F1_26TrackIdMadring)
                        info.ContentPack2026 = true;
                    int realCars = 0;
                    foreach (var te in s.TeamByCarIdx.Values)
                    {
                        if (te == null || te.TeamId == 255) continue; // 255 = slot vazio/inválido
                        realCars++;
                        if (GameInfo.IsF1_26TeamId(te.TeamId))
                            info.ContentPack2026 = true;
                    }
                    if (realCars > 20) info.ContentPack2026 = true; // grid de 11 equipes (F1 26)

                    // Packet format mais novo vence (e carrega os números de versão dele).
                    if (s.LastPacketFormat != 0 && s.LastPacketFormat >= info.NewestPacketFormat)
                    {
                        info.NewestPacketFormat = s.LastPacketFormat;
                        info.NewestGameYear = s.LastGameYear;
                        info.NewestGameMajor = s.LastGameMajorVersion;
                        info.NewestGameMinor = s.LastGameMinorVersion;
                    }
                    if (s.LastGameYear >= 26) anyGameYear2026 = true;
                }
            }

            info.FormatLabel = (info.NewestPacketFormat != 0)
                ? GameInfo.GameNameFromPacketFormat(info.NewestPacketFormat)
                : "F1_25";

            // Conteúdo primeiro; senão o formato; senão o ano reportado; senão F1_25.
            if (info.ContentPack2026) info.GameLabel = "F1_26";
            else if (info.NewestPacketFormat != 0) info.GameLabel = info.FormatLabel;
            else if (anyGameYear2026) info.GameLabel = "F1_26";
            else info.GameLabel = "F1_25";

            return info;
        }

        /// <summary>Conveniência: só o rótulo final (usado pelo snapshot ao vivo).</summary>
        public static string DetectGameLabel(IEnumerable<SessionRun> sessions)
        {
            return Resolve(sessions).GameLabel;
        }
    }
}

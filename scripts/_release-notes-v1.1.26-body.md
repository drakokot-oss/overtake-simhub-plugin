### Fixed
- **Qualificação online — P20 `Driver_19` fantasma (lobby com 19 pilotos):** o FC lista 22 slots; índices `carIdx >= NumActiveCars` do **Participants** (pico, só packet 4) com tag genérica e sem volta/tempo são **ignorados**. Export inclui `participantsPeakNumActive` na sessão (paridade Python `session_store` + `league_finalizer`)

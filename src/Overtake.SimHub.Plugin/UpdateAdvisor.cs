using System;

namespace Overtake.SimHub.Plugin
{
    /// <summary>
    /// How urgently the user should update the plugin. Ordered by ascending
    /// severity so callers can compare with &gt;.
    /// </summary>
    public enum UpdateSeverity
    {
        /// <summary>Running the latest (or newer) version. No banner.</summary>
        UpToDate = 0,

        /// <summary>A newer version exists but the current one is still above the
        /// supported floor. Friendly "update available" banner.</summary>
        UpdateAvailable = 1,

        /// <summary>The running version is BELOW the publisher's minimum supported
        /// version (<c>minSupportedVersion</c> in version.json). Known to produce
        /// broken/unreliable exports (e.g. a pre-2026 build fed F1 26 wire format).
        /// Loud, blocking-style warning.</summary>
        UpdateRequired = 2,

        /// <summary>The running build is, RIGHT NOW, receiving a UDP wire format
        /// it does not know how to parse (<c>SessionStore.UnsupportedFormatSeen</c>
        /// != 0). The export will be garbage. Highest severity because it is a
        /// confirmed live failure, not just a version gap.</summary>
        UnsupportedFormat = 3,
    }

    /// <summary>
    /// Pure, dependency-free decision logic for the in-plugin update advisory.
    /// Kept free of any SimHub / WPF references so it can be unit-tested by the
    /// PowerShell harness (loaded via reflection from the built DLL) without a
    /// running SimHub host.
    ///
    /// v1.1.44 — introduced after a user on a very old build (v1.1.27) captured
    /// an F1 26 (UDP Format 2026) session: the 2025-only parser produced an .otk
    /// with scrambled names/teamIds. The container was fine; the data was garbage.
    /// The passive yellow "update available" banner did not convey that risk, so
    /// we escalate severity (and tie it to live unsupported-format detection).
    /// </summary>
    public static class UpdateAdvisor
    {
        /// <summary>
        /// Decide how urgently the user should update.
        /// </summary>
        /// <param name="currentVersion">The running plugin version, e.g. "1.1.27" or "1.1.27.0".</param>
        /// <param name="latestVersion">The latest published version from version.json (may be null/empty if the check failed).</param>
        /// <param name="minSupportedVersion">The publisher's supported floor from version.json (optional).</param>
        /// <param name="unsupportedFormatSeen">A UDP wire format the running build cannot parse, observed live (0 = none).</param>
        public static UpdateSeverity Evaluate(
            string currentVersion,
            string latestVersion,
            string minSupportedVersion,
            int unsupportedFormatSeen)
        {
            // A confirmed live failure outranks any version comparison: the file
            // being produced this very session is unreadable.
            if (unsupportedFormatSeen != 0)
                return UpdateSeverity.UnsupportedFormat;

            Version current = TryParse(currentVersion);
            if (current == null)
                return UpdateSeverity.UpToDate; // can't reason about an unknown current version

            // Below the publisher's supported floor -> loud warning, even if the
            // network check for "latest" failed (min ships in the same json but we
            // guard each field independently).
            Version min = TryParse(minSupportedVersion);
            if (min != null && current < min)
                return UpdateSeverity.UpdateRequired;

            Version latest = TryParse(latestVersion);
            if (latest != null && latest > current)
                return UpdateSeverity.UpdateAvailable;

            return UpdateSeverity.UpToDate;
        }

        /// <summary>
        /// Stable machine-readable token for the dashboard property
        /// <c>Overtake.UpdateStatus</c>. Kept ASCII and dash-free so it is easy to
        /// match in NCalc / dashboard expressions.
        /// </summary>
        public static string StatusToken(UpdateSeverity severity)
        {
            switch (severity)
            {
                case UpdateSeverity.UnsupportedFormat: return "UnsupportedFormat";
                case UpdateSeverity.UpdateRequired:    return "UpdateRequired";
                case UpdateSeverity.UpdateAvailable:   return "UpdateAvailable";
                default:                               return "UpToDate";
            }
        }

        /// <summary>
        /// Parses "X", "X.Y", "X.Y.Z" or "X.Y.Z.W" into a comparable
        /// <see cref="Version"/>, normalizing to four components so comparisons
        /// are stable regardless of how many parts each side carries. Returns null
        /// for null/empty/garbage input (caller decides the fallback).
        /// </summary>
        private static Version TryParse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V'))
                s = s.Substring(1);

            int major = 0, minor = 0, build = 0, revision = 0;
            string[] parts = s.Split('.');
            try
            {
                if (parts.Length >= 1 && !int.TryParse(parts[0], out major)) return null;
                if (parts.Length >= 2 && !int.TryParse(parts[1], out minor)) return null;
                if (parts.Length >= 3 && !int.TryParse(parts[2], out build)) return null;
                if (parts.Length >= 4 && !int.TryParse(parts[3], out revision)) return null;
                if (major < 0 || minor < 0 || build < 0 || revision < 0) return null;
                return new Version(major, minor, build, revision);
            }
            catch
            {
                return null;
            }
        }
    }
}

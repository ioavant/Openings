using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace Openings
{
    internal class UpdateInfo
    {
        public string Version { get; set; }
        public string DownloadUrl { get; set; }
        public string ReleaseNotes { get; set; }
    }

    /// <summary>
    /// Looks up the published version once per Revit session and, if it is newer,
    /// exposes it for the main window's banner. Nothing is ever downloaded or
    /// installed automatically — the banner is a link.
    /// </summary>
    internal static class UpdateChecker
    {
        // Raw URL to version.json on the default branch of the public source
        // repository, which is also where the releases live. Releases used to be
        // kept in a separate repository because this one was private.
        private const string VersionUrl =
            "https://raw.githubusercontent.com/ioavant/Openings/main/version.json";

        public static UpdateInfo AvailableUpdate { get; private set; }

        /// <summary>Version of the running build, for display ("1.0.0").</summary>
        public static string CurrentVersion
        {
            get
            {
                Version version = Assembly.GetExecutingAssembly().GetName().Version;
                return version == null ? "" : version.Major + "." + version.Minor + "." + version.Build;
            }
        }

        public static void CheckInBackground()
        {
            // TES builds are distributed internally and have no public release feed, so the whole
            // mechanism stays dormant: no request, and AvailableUpdate stays null, which keeps the
            // update banner out of the window.
            if (!Brand.UpdateCheckEnabled) return;

            Task.Run(() =>
            {
                try { DoCheck(); }
                catch { /* network unavailable, or nothing published yet - ignore */ }
            });
        }

        private static void DoCheck()
        {
            // HttpClient rather than WebClient: the net8 build warns on WebClient,
            // and this runs on a background task where blocking on the result is
            // exactly as harmless as the synchronous call it replaces.
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(15);
                client.DefaultRequestHeaders.Add("User-Agent", "Openings-UpdateChecker/1.0");
                string json = client.GetStringAsync(VersionUrl).GetAwaiter().GetResult();
                UpdateInfo info = ParseJson(json);
                if (info == null || info.Version == null) return;

                Version current = Assembly.GetExecutingAssembly().GetName().Version;
                Version remote;
                if (Version.TryParse(info.Version, out remote) && remote > current)
                    AvailableUpdate = info;
            }
        }

        // Minimal hand-rolled JSON value extractor - no external dependencies needed for three keys.
        private static UpdateInfo ParseJson(string json)
        {
            try
            {
                return new UpdateInfo
                {
                    Version = ExtractString(json, "version"),
                    DownloadUrl = ExtractString(json, "downloadUrl"),
                    ReleaseNotes = ExtractString(json, "releaseNotes") ?? string.Empty
                };
            }
            catch { return null; }
        }

        private static string ExtractString(string json, string key)
        {
            int keyIdx = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (keyIdx < 0) return null;
            int colon = json.IndexOf(':', keyIdx);
            if (colon < 0) return null;
            int open = json.IndexOf('"', colon);
            if (open < 0) return null;
            int close = json.IndexOf('"', open + 1);
            if (close < 0) return null;
            return json.Substring(open + 1, close - open - 1);
        }
    }
}

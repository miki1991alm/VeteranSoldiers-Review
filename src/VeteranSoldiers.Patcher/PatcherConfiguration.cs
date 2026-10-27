using System.IO;

namespace VeteranSoldiers.Patching
{
    /// <summary>Read-only subset of the installed BepInEx 5 ConfigFile format.</summary>
    public static class PatcherConfiguration
    {
        public const string FileName = "com.veteransoldiers.mod.cfg";

        public static bool IsEnabled(string path)
        {
            if (!File.Exists(path)) return true;
            using (var reader = File.OpenText(path)) return ReadEnabled(reader);
        }

        public static bool ReadEnabled(TextReader reader)
        {
            string section = string.Empty;
            string? value = null;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("#", System.StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", System.StringComparison.Ordinal) && line.EndsWith("]", System.StringComparison.Ordinal))
                {
                    section = line.Substring(1, line.Length - 2);
                    continue;
                }
                int separator = line.IndexOf('=');
                if (section == "General" && separator >= 0 && line.Substring(0, separator).Trim() == "Enabled")
                    value = line.Substring(separator + 1).Trim();
            }
            // BepInEx definitions are case-sensitive; the last duplicate wins.
            // Invalid or missing bool values retain the runtime's default (true).
            return !bool.TryParse(value, out bool enabled) || enabled;
        }
    }
}

using System;
using System.IO;
using System.Text.Json;
using Sowser.Models;

namespace Sowser.Services
{
    public static class AppSettingsStore
    {
        private static string SettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sowser", "appsettings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string json = File.ReadAllText(SettingsPath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null)
                    {
                        s.ReadLater ??= new System.Collections.Generic.List<Models.ReadLaterItem>();
                        s.CustomQuickLinks ??= new System.Collections.Generic.List<Models.QuickLinkItem>();
                        return s;
                    }
                }
            }
            catch { /* default */ }

            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                string dir = Path.GetDirectoryName(SettingsPath)!;
                Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch { /* silent */ }
        }
    }
}

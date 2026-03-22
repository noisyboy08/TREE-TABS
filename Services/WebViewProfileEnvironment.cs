using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Sowser.Services
{
    public static class WebViewProfileEnvironment
    {
        private static readonly Dictionary<string, Task<CoreWebView2Environment>> Cache = new();

        public static Task<CoreWebView2Environment> GetAsync(string profileKey)
        {
            string safe = string.IsNullOrWhiteSpace(profileKey) ? "default" : string.Join("_", profileKey.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(safe)) safe = "default";

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Sowser", "WebView2", safe);
            Directory.CreateDirectory(root);

            lock (Cache)
            {
                if (!Cache.TryGetValue(root, out Task<CoreWebView2Environment>? task))
                {
                    task = CoreWebView2Environment.CreateAsync(userDataFolder: root);
                    Cache[root] = task;
                }
                return task;
            }
        }
    }
}

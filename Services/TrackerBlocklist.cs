using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace Sowser.Services
{
    public static class TrackerBlocklist
    {
        private static readonly ConditionalWeakTable<CoreWebView2, object?> Subscribed = new();

        private static readonly string[] HostFragments =
        {
            "doubleclick.net", "googlesyndication.com", "googleadservices.com",
            "facebook.net", "scorecardresearch.com", "adservice.google", "adsafeprotected",
            "advertising.com", "taboola.com", "outbrain.com", "criteo.com", "adsystem",
            "analytics.google", "hotjar.com", "segment.io", "segment.com", "optimizely",
            "clarity.ms", "bat.bing.com", "ads.linkedin.com"
        };

        private static readonly object Gate = new();

        public static void AttachIfEnabled(CoreWebView2? core)
        {
            if (core == null || !AppServices.BlockTrackers) return;
            if (Subscribed.TryGetValue(core, out _)) return;
            Subscribed.Add(core, null);

            lock (Gate)
            {
                try
                {
                    core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                }
                catch
                {
                    /* filter may already exist */
                }

                core.WebResourceRequested += OnWebResourceRequested;
            }
        }

        private static void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                if (sender is not CoreWebView2 core) return;
                var uri = new Uri(e.Request.Uri);
                string host = uri.Host.ToLowerInvariant();
                if (HostFragments.Any(f => host.Contains(f, StringComparison.OrdinalIgnoreCase)))
                {
                    e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain\r\n");
                }
            }
            catch
            {
                /* ignore */
            }
        }
    }
}

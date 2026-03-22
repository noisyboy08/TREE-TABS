using System.Collections.Generic;

namespace Sowser.Models
{
    public class AppSettings
    {
        public string DefaultSearchEngine { get; set; } = "https://www.google.com/search?q=";
        public string Theme { get; set; } = "Light";
        public string CanvasBackground { get; set; } = "#F5F5F5";
        public bool AutoSaveEnabled { get; set; } = true;
        public int AutoSaveIntervalSeconds { get; set; } = 30;

        /// <summary>Prompt | Silent | Off</summary>
        public string SessionRestoreMode { get; set; } = "Prompt";

        public bool BlockTrackers { get; set; }
        public bool SuspendOffscreenCards { get; set; } = true;
        public bool TimeMachineSnapshotsEnabled { get; set; } = true;
        public string DefaultBrowserProfile { get; set; } = "default";

        public List<ReadLaterItem> ReadLater { get; set; } = new();
        public List<QuickLinkItem> CustomQuickLinks { get; set; } = new();
    }
}

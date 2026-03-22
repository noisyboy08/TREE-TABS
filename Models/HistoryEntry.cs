using System;

namespace Sowser.Models
{
    /// <summary>
    /// Represents a browsing history entry
    /// </summary>
    public class HistoryEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public DateTime VisitedAt { get; set; } = DateTime.Now;
    }
}

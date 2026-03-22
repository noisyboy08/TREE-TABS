using System;

namespace Sowser.Models
{
    /// <summary>
    /// Represents a connection between two browser cards (link navigation)
    /// </summary>
    public class Connection
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string FromCardId { get; set; } = string.Empty;
        public string ToCardId { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string FromEdge { get; set; } = string.Empty;
        public string ToEdge { get; set; } = string.Empty;
        public bool IsManual { get; set; }
    }
}

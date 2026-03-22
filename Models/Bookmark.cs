using System;

namespace Sowser.Models
{
    /// <summary>
    /// Represents a bookmarked page
    /// </summary>
    public class Bookmark
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}

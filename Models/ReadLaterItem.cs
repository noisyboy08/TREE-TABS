using System;

namespace Sowser.Models
{
    public class ReadLaterItem
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public DateTime AddedAt { get; set; } = DateTime.Now;
    }
}

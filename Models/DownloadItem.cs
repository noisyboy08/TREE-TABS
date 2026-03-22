using System;

namespace Sowser.Models
{
    /// <summary>
    /// Represents a download item
    /// </summary>
    public class DownloadItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string FileName { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public long ReceivedBytes { get; set; }
        public DownloadState State { get; set; } = DownloadState.InProgress;
        public DateTime StartedAt { get; set; } = DateTime.Now;
    }

    public enum DownloadState
    {
        InProgress,
        Completed,
        Cancelled,
        Failed
    }
}

namespace Sowser.Models
{
    /// <summary>
    /// Serializable state of a browser card for workspace save/load
    /// </summary>
    public class CardState
    {
        public string Id { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? GroupId { get; set; }
        public string BrowserProfile { get; set; } = "default";
        public bool IsPortal { get; set; }
        public string PortalWorkspaceFullPath { get; set; } = string.Empty;
        public string PortalTargetFile { get; set; } = string.Empty;
    }
}

using System.Windows.Media;

namespace Sowser.Models
{
    public class BrowserCardModel
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public SolidColorBrush? GroupColor { get; set; }
        public string? GroupName { get; set; }
    }
}

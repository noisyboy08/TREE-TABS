using System;
using System.Collections.Generic;

namespace Sowser.Models
{
    /// <summary>
    /// Represents a named color group that browser cards can belong to
    /// </summary>
    public class CardGroup
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Group";
        public string Color { get; set; } = "#00D9FF"; // accent hex color
        public List<string> CardIds { get; set; } = new();
    }
}

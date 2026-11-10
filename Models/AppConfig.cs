using System.Collections.Generic;

namespace ModelCopier.Models
{
    /// <summary>Root object serialised to / from models.json.</summary>
    public class AppConfig
    {
        public List<ModelEntry> Models { get; set; } = new();
    }
}

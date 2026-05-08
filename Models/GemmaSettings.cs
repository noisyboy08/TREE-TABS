namespace Sowser.Models
{
    public class GemmaSettings
    {
        public bool UseLocalOllama { get; set; } = true;
        public string OllamaEndpoint { get; set; } = "http://localhost:11434";
        public string OllamaModel { get; set; } = "gemma3:4b";
        public string GeminiApiKey { get; set; } = "";
        public string GeminiModel { get; set; } = "gemma-3-4b-it";
        public bool IsEnabled { get; set; } = true;
    }
}

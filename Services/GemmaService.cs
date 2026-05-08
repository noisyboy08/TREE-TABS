using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Sowser.Models;

namespace Sowser.Services
{
    public class GemmaService
    {
        private const string SystemPrompt =
            "You are a browser tab organizer. You will receive a JSON list of open browser tabs with their titles and URLs. Group them into 2-6 meaningful clusters based on topic or purpose. Respond ONLY with a valid JSON array. No explanation, no markdown, no code fences. Each element must have: groupName (string), color (hex string like #FF6B6B), urls (array of url strings from the input).";

        private readonly GemmaSettings _settings;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public GemmaService(GemmaSettings settings)
        {
            _settings = settings;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        public async Task<List<CardGroup>> GetGroupsAsync(List<CardInfo> cards)
        {
            try
            {
                if (cards.Count == 0)
                    return new List<CardGroup>();

                string tabsJson = JsonSerializer.Serialize(
                    cards.Select(c => new { title = c.Title, url = c.Url }),
                    new JsonSerializerOptions { WriteIndented = true });

                string prompt = $"SYSTEM:\n\"{SystemPrompt}\"\n\nUSER:\n{tabsJson}";
                string modelText = _settings.UseLocalOllama
                    ? await GetOllamaResponseAsync(prompt)
                    : await GetGeminiResponseAsync(prompt);

                return ParseGroups(modelText);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GemmaService.GetGroupsAsync failed: {ex}");
                return new List<CardGroup>();
            }
        }

        private async Task<string> GetOllamaResponseAsync(string prompt)
        {
            string endpoint = (_settings.OllamaEndpoint ?? "").TrimEnd('/');
            var body = new
            {
                model = _settings.OllamaModel,
                prompt,
                stream = false
            };

            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync($"{endpoint}/api/generate", content);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("response", out var responseElement)
                ? responseElement.GetString() ?? string.Empty
                : string.Empty;
        }

        private async Task<string> GetGeminiResponseAsync(string prompt)
        {
            if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
                return string.Empty;

            string model = string.IsNullOrWhiteSpace(_settings.GeminiModel)
                ? "gemma-3-4b-it"
                : _settings.GeminiModel;

            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(_settings.GeminiApiKey)}";
            var body = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[] { new { text = prompt } }
                    }
                }
            };

            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? string.Empty;
        }

        private List<CardGroup> ParseGroups(string text)
        {
            try
            {
                string json = StripMarkdownFences(text);
                var groups = JsonSerializer.Deserialize<List<CardGroup>>(json, _jsonOptions);
                var parsedGroups = groups?
                    .Where(g => !string.IsNullOrWhiteSpace(g.GroupName) && g.Urls.Count > 0)
                    .ToList() ?? new List<CardGroup>();

                foreach (var group in parsedGroups.Where(g => !IsHexColor(g.Color)))
                {
                    group.Color = "#00D9FF";
                }

                return parsedGroups;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GemmaService.ParseGroups failed: {ex}");
                return new List<CardGroup>();
            }
        }

        private static string StripMarkdownFences(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
                return trimmed;

            int firstNewLine = trimmed.IndexOf('\n');
            int lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine)
                return trimmed.Substring(firstNewLine + 1, lastFence - firstNewLine - 1).Trim();

            return trimmed.Trim('`').Trim();
        }

        private static bool IsHexColor(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 7 || value[0] != '#')
                return false;

            for (int i = 1; i < value.Length; i++)
            {
                char c = value[i];
                bool isHex = (c >= '0' && c <= '9') ||
                             (c >= 'a' && c <= 'f') ||
                             (c >= 'A' && c <= 'F');
                if (!isHex)
                    return false;
            }

            return true;
        }
    }
}

using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CharacterCardManager.Models;

namespace CharacterCardManager.Services;

public sealed class LlmChatService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    public sealed record ChatMessage(string Role, string Content);

    public static string BuildCardContext(CharacterCardData card)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a helpful assistant analyzing a TavernAI / SillyTavern character card.");
        sb.AppendLine("Answer the user's questions accurately based only on the card data below.");
        sb.AppendLine();
        sb.AppendLine($"Name: {card.Name}");
        if (!string.IsNullOrWhiteSpace(card.Creator)) sb.AppendLine($"Creator: {card.Creator}");
        if (!string.IsNullOrWhiteSpace(card.Tagline)) sb.AppendLine($"Tagline: {card.Tagline}");
        if (card.Tags.Count > 0) sb.AppendLine($"Tags: {string.Join(", ", card.Tags)}");
        sb.AppendLine($"Spec: {card.SpecVersion} | Lorebook entries: {card.LorebookCount} | Alternate greetings: {card.AlternateGreetings.Count}");

        AppendSection(sb, "Description", card.Description);
        AppendSection(sb, "Personality", card.Personality);
        AppendSection(sb, "Scenario", card.Scenario);
        AppendSection(sb, "First Message", card.FirstMessage);
        AppendSection(sb, "System Prompt", card.SystemPrompt);
        AppendSection(sb, "Post-History Instructions", card.PostHistoryInstructions);
        AppendSection(sb, "Creator Notes", card.CreatorNotes);

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string title, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.AppendLine();
        sb.AppendLine($"[{title}]");
        sb.AppendLine(value.Trim());
    }

    public async Task<string> AskAsync(
        string endpointUrl,
        string? model,
        string? apiKey,
        CharacterCardData card,
        IReadOnlyList<ChatMessage> history,
        CancellationToken ct = default)
    {
        string baseUrl = endpointUrl.Trim().TrimEnd('/');
        string url = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
                ? $"{baseUrl}/chat/completions"
                : $"{baseUrl}/v1/chat/completions");

        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = BuildCardContext(card)
            }
        };

        foreach (var msg in history)
        {
            messages.Add(new JsonObject
            {
                ["role"] = msg.Role,
                ["content"] = msg.Content
            });
        }

        var payload = new JsonObject
        {
            ["messages"] = messages,
            ["temperature"] = 0.4
        };

        if (!string.IsNullOrWhiteSpace(model))
            payload["model"] = model.Trim();

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var resp = await Http.SendAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msgEl) &&
                msgEl.TryGetProperty("content", out var contentEl))
            {
                return contentEl.GetString()?.Trim() ?? string.Empty;
            }
        }

        throw new InvalidDataException("Unexpected response format from LLM endpoint.");
    }
}

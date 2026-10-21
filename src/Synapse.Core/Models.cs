using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Synapse;

// MARK: - Providers & model references

public enum ProviderId { Local, Google, Groq, Openrouter, Custom }

public static class Providers
{
    public static readonly ProviderId[] Cloud = [ProviderId.Google, ProviderId.Groq, ProviderId.Openrouter, ProviderId.Custom];

    public static string Key(this ProviderId p) => p.ToString().ToLowerInvariant();

    public static ProviderId? Parse(string? s) =>
        Enum.TryParse<ProviderId>(s, ignoreCase: true, out var p) ? p : null;

    public static string DisplayName(this ProviderId p) => p switch
    {
        ProviderId.Local => "On this PC",
        ProviderId.Google => "Google AI Studio",
        ProviderId.Groq => "Groq",
        ProviderId.Openrouter => "OpenRouter",
        _ => "Custom (OpenAI-compatible)",
    };

    public static string BaseUrl(this ProviderId p) => p switch
    {
        ProviderId.Local => $"http://127.0.0.1:{LocalEngine.Port}/v1",
        ProviderId.Google => "https://generativelanguage.googleapis.com/v1beta/openai",
        ProviderId.Groq => "https://api.groq.com/openai/v1",
        ProviderId.Openrouter => "https://openrouter.ai/api/v1",
        _ => AppSettings.Shared.CustomBaseUrl.TrimEnd('/'),
    };

    /// Where the user signs in to create a key.
    public static string? KeyPageUrl(this ProviderId p) => p switch
    {
        ProviderId.Google => "https://aistudio.google.com/apikey",
        ProviderId.Groq => "https://console.groq.com/keys",
        ProviderId.Openrouter => "https://openrouter.ai/settings/keys",
        _ => null,
    };

    public static string? UsagePageUrl(this ProviderId p) => p switch
    {
        ProviderId.Google => "https://aistudio.google.com/usage",
        ProviderId.Groq => "https://console.groq.com/settings/limits",
        ProviderId.Openrouter => "https://openrouter.ai/activity",
        _ => null,
    };

    public static string FreeTierNote(this ProviderId p) => p switch
    {
        ProviderId.Google => "Free tier with per-minute and per-day request limits per model.",
        ProviderId.Groq => "Free tier, very fast. Limits are reported live with every reply.",
        ProviderId.Openrouter => "Models ending in “:free” cost nothing (about 50 requests/day without credits).",
        ProviderId.Custom => "Any server that speaks the OpenAI chat API.",
        _ => "Runs privately on your PC. No key, no limits.",
    };

    public static string KeySteps(this ProviderId p) => p switch
    {
        ProviderId.Google => "Sign in with your Google account → “Create API key” → copy it.",
        ProviderId.Groq => "Sign in (Google, GitHub or email) → “Create API Key” → copy it.",
        ProviderId.Openrouter => "Sign in → “Create Key” → copy it. Free models end in “:free”.",
        _ => "",
    };

    public static bool NeedsKey(this ProviderId p) => p != ProviderId.Local;
}

public sealed record ModelRef
{
    public ProviderId Provider { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Vision { get; init; }

    [JsonIgnore] public string QualifiedId => $"{Provider.Key()}/{Id}";

    public ModelRef() { }
    public ModelRef(ProviderId provider, string id, string name, bool vision = false)
    {
        Provider = provider; Id = id; Name = name; Vision = vision;
    }
}

// MARK: - Chats

public enum ThinkLevel { Off, Standard, High, Max }

public static class ThinkLevels
{
    public static string Label(this ThinkLevel l) => l switch
    {
        ThinkLevel.Off => "Fast",
        ThinkLevel.Standard => "Normal",
        ThinkLevel.High => "Think hard",
        _ => "Think harder",
    };

    public static string Help(this ThinkLevel l) => l switch
    {
        ThinkLevel.Off => "No extra reasoning. Quickest answers.",
        ThinkLevel.Standard => "The model decides how much to think.",
        ThinkLevel.High => "Maximum reasoning budget and step-by-step instructions.",
        _ => "Think hard, then a second pass reviews and corrects the answer.",
    };
}

public sealed class Capabilities
{
    public bool WebSearch { get; set; } = true;
    public bool ImageGeneration { get; set; }
    public bool ComputerAccess { get; set; }
    public bool Browser { get; set; }
    public bool Websites { get; set; }

    public Capabilities Clone() => (Capabilities)MemberwiseClone();
}

public enum AttachmentKind { Image, Text, Pdf, Other }

public sealed class Attachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AttachmentKind Kind { get; set; }
    public string Filename { get; set; } = "";
    public string Path { get; set; } = "";
    public string? ExtractedText { get; set; }
}

public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
    /// Provider-specific data that must be echoed back (e.g. Gemini thought signatures).
    public JsonNode? Extra { get; set; }
}

public enum Role { System, User, Assistant, Tool }

public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Role Role { get; set; }
    public string Content { get; set; } = "";
    /// Kept for context, never shown.
    public string Reasoning { get; set; } = "";
    public double? ThinkingSeconds { get; set; }
    /// e.g. "Loading model…"
    public string? Status { get; set; }
    public List<Attachment> Attachments { get; set; } = [];
    public List<ToolCall> ToolCalls { get; set; } = [];
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    /// Generated/screenshot images to show (and to send to vision models).
    public List<string> Images { get; set; } = [];
    /// Files the model asked to show the user.
    public List<string> PresentedFiles { get; set; } = [];
    /// e.g. localhost site URLs.
    public List<string> Links { get; set; } = [];
    public bool IsError { get; set; }
    public string? ModelName { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ChatMessage() { }
    public ChatMessage(Role role, string content = "") { Role = role; Content = content; }
}

public sealed class Chat
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New Chat";
    public Guid? ProjectId { get; set; }
    public ModelRef? Model { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
    public Capabilities Capabilities { get; set; } = new();
    public ThinkLevel Think { get; set; } = ThinkLevel.Standard;
    public bool AutoApprove { get; set; }
    public bool Pinned { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Instructions { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// MARK: - Local models

public enum ModelFormat { Gguf }

public sealed class LocalModel
{
    /// e.g. "unsloth/Qwen3.5-4B-GGUF:Q4_K_M"
    public string Id { get; set; } = "";
    public string Repo { get; set; } = "";
    public ModelFormat Format { get; set; } = ModelFormat.Gguf;
    public string DisplayName { get; set; } = "";
    /// Folder on disk.
    public string Directory { get; set; } = "";
    /// First model file, relative to Directory.
    public string? MainFile { get; set; }
    /// Vision projector, relative to Directory.
    public string? Mmproj { get; set; }
    public long SizeBytes { get; set; }
    public DateTime DownloadedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public bool Vision => Mmproj != null;
    [JsonIgnore] public ModelRef Ref => new(ProviderId.Local, Id, DisplayName, Vision);
}

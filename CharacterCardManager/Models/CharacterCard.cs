using System.ComponentModel;
using System.Text.Json.Nodes;

namespace CharacterCardManager.Models;

public sealed class CardSummary : INotifyPropertyChanged
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long LastModifiedTicks { get; set; }
    public string SpecVersion { get; set; } = "v2";
    public long? BotbooruPostId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string FirstMessage { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public int LorebookCount { get; set; }
    public int AlternateGreetingCount { get; set; }
    public int GalleryCount { get; set; }
    public bool IsFork { get; set; }
    public int ForkCount { get; set; }
    public int SiblingCount { get; set; }
    public double? SlopScore { get; set; }
    public bool IsBotbooruSynced { get; set; }
    public bool HasOriginalTag { get; set; }

    public string BotbooruDisplay => BotbooruPostId.HasValue ? $"#{BotbooruPostId.Value}" : "";
    public string MetaLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Creator))
                parts.Add(Creator);
            parts.Add(SpecVersion.ToUpperInvariant());
            if (IsFork)
                parts.Add("Fork");
            else if (ForkCount > 0)
                parts.Add($"Master({ForkCount})");
            if (SiblingCount > 0)
                parts.Add($"Siblings:{SiblingCount}");
            if (LorebookCount > 0)
                parts.Add($"Lore:{LorebookCount}");
            if (GalleryCount > 0)
                parts.Add($"Gallery:{GalleryCount}");
            parts.Add($"{Tags.Count} tags");
            return string.Join(" • ", parts);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyUpdated()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetaLine)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBotbooruSynced)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GalleryCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFork)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ForkCount)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SiblingCount)));
    }
}

public sealed class CharacterCardData
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long LastModifiedTicks { get; set; }
    public string SpecVersion { get; set; } = "v2";
    public bool HasCcv3Chunk { get; set; }
    public long? BotbooruPostId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Creator { get; set; } = string.Empty;
    public string CharacterVersion { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string FirstMessage { get; set; } = string.Empty;
    public string MessageExample { get; set; } = string.Empty;
    public string CreatorNotes { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string PostHistoryInstructions { get; set; } = string.Empty;
    public List<string> AlternateGreetings { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public int LorebookCount { get; set; }

    /// <summary>
    /// Preserves the original JSON structure (lorebooks, extensions.chub, extensions.botbooru, etc.)
    /// so editing fields never strips unknown properties.
    /// </summary>
    public JsonObject? RawRoot { get; set; }

    public CardSummary ToSummary() => new()
    {
        FilePath = FilePath,
        FileName = FileName,
        FileSize = FileSize,
        LastModifiedTicks = LastModifiedTicks,
        SpecVersion = SpecVersion,
        BotbooruPostId = BotbooruPostId,
        Name = Name,
        Creator = Creator,
        Tagline = Tagline,
        Description = Description,
        Personality = Personality,
        Scenario = Scenario,
        FirstMessage = FirstMessage,
        Tags = [.. Tags],
        LorebookCount = LorebookCount,
        AlternateGreetingCount = AlternateGreetings.Count,
        HasOriginalTag = Tags.Any(t => string.Equals(t, "original", StringComparison.OrdinalIgnoreCase))
    };
}

public sealed class TagChipItem
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "Uncategorized";
    public string ColorHex { get; set; } = "#6E6E8C";
}

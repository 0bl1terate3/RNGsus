using System;
using System.Collections.Generic;

namespace BiomeMacro.Models;

public enum BiomeType
{
    // Standard Biomes
    Normal,
    Sandstorm,
    Hell,
    Starfall,
    Heaven,
    Corruption,
    Null,
    Glitched,
    Dreamspace,
    Cyberspace,
    
    // Weather Conditions
    Windy,
    Snowy,
    Rainy,
    
    // Event Biomes
    PumpkinMoon,
    Graveyard,
    BloodRain,
    Aurora,
    
    Unknown
}

public class BiomeMetadata
{
    public string DisplayName { get; init; } = "";
    public string SpawnChance { get; init; } = "";
    public string Duration { get; init; } = "";
    public string Color { get; init; } = "#FFFFFF";
    public int Rarity { get; init; } = 0; // 0 = common, higher = rarer
    public double Multiplier { get; init; } = 1.0;
    public string[] Keywords { get; init; } = Array.Empty<string>();
}

public class BiomeInfo
{
    public BiomeType Type { get; set; } = BiomeType.Normal;
    public string Name { get; set; } = "Normal";
    public DateTime DetectedAt { get; set; } = DateTime.Now;
    public string Source { get; set; } = "Unknown"; // "Bloxstrap" or "Roblox"
    
    public BiomeMetadata Metadata => BiomeDatabase.GetMetadata(Type);
    
    public override string ToString() => $"{Name} ({DetectedAt:HH:mm:ss})";
}

public static class BiomeDatabase
{
    private static readonly Dictionary<BiomeType, BiomeMetadata> _biomes = new()
    {
        // Standard Biomes
        [BiomeType.Normal] = new BiomeMetadata
        {
            DisplayName = "Normal",
            SpawnChance = "Default",
            Duration = "Permanent",
            Color = "#7CB342",
            Rarity = 0,
            Multiplier = 1.0,
            Keywords = new[] { "normal", "default", "base" }
        },
        [BiomeType.Sandstorm] = new BiomeMetadata
        {
            DisplayName = "Sandstorm",
            SpawnChance = "1/3,000/sec",
            Duration = "~11 min",
            Color = "#FFB74D",
            Rarity = 3,
            Multiplier = 4.0,
            Keywords = new[] { "sandstorm", "sand storm", "desert" }
        },
        [BiomeType.Hell] = new BiomeMetadata
        {
            DisplayName = "Hell",
            SpawnChance = "1/6,666/sec",
            Duration = "~11 min",
            Color = "#F44336",
            Rarity = 4,
            Multiplier = 6.0,
            Keywords = new[] { "hell", "inferno", "lava" }
        },
        [BiomeType.Starfall] = new BiomeMetadata
        {
            DisplayName = "Starfall",
            SpawnChance = "1/7,500/sec",
            Duration = "Variable",
            Color = "#7C4DFF",
            Rarity = 5,
            Multiplier = 5.0,
            Keywords = new[] { "starfall", "star fall", "falling stars" }
        },
        [BiomeType.Heaven] = new BiomeMetadata
        {
            DisplayName = "Heaven",
            SpawnChance = "Rare",
            Duration = "Variable",
            Color = "#FFEB3B",
            Rarity = 6,
            Multiplier = 2.0, // Estimated (SolsScope doesn't listing Heaven explicitly but likely high)
            Keywords = new[] { "heaven", "heavenly", "divine" }
        },
        [BiomeType.Corruption] = new BiomeMetadata
        {
            DisplayName = "Corruption",
            SpawnChance = "1/9,000/sec",
            Duration = "~11 min",
            Color = "#9C27B0",
            Rarity = 5,
            Multiplier = 5.0,
            Keywords = new[] { "corruption", "corrupt", "corrupted" }
        },
        [BiomeType.Null] = new BiomeMetadata
        {
            DisplayName = "Null",
            SpawnChance = "1/10,100/sec",
            Duration = "Variable",
            Color = "#9E9E9E",
            Rarity = 6,
            Multiplier = 1000.0,
            Keywords = new[] { "null", "void", "undefined" }
        },
        [BiomeType.Glitched] = new BiomeMetadata
        {
            DisplayName = "Glitched",
            SpawnChance = "1/30,000 on change",
            Duration = "Variable",
            Color = "#00E676",
            Rarity = 8,
            Multiplier = 1.0, // SolsScope says 1? Keeping it consistent.
            Keywords = new[] { "glitched", "glitch", "error" }
        },
        [BiomeType.Dreamspace] = new BiomeMetadata
        {
            DisplayName = "Dreamspace",
            SpawnChance = "1/3,500,000/sec",
            Duration = "~3 min",
            Color = "#FF69B4",
            Rarity = 10,
            Multiplier = 1.0,
            Keywords = new[] { "dreamspace", "dream space" }
        },
        [BiomeType.Cyberspace] = new BiomeMetadata
        {
            DisplayName = "Cyberspace",
            SpawnChance = "1/5,000 (controller)",
            Duration = "~12 min",
            Color = "#00FFFF",
            Rarity = 7,
            Multiplier = 2.0,
            Keywords = new[] { "cyberspace", "cyber", "digital" }
        },
        
        // Weather Conditions
        [BiomeType.Windy] = new BiomeMetadata
        {
            DisplayName = "Windy",
            SpawnChance = "1/500/sec",
            Duration = "Variable",
            Color = "#B0BEC5",
            Rarity = 1,
            Multiplier = 3.0,
            Keywords = new[] { "windy", "wind", "gusty" }
        },
        [BiomeType.Snowy] = new BiomeMetadata
        {
            DisplayName = "Snowy",
            SpawnChance = "1/750/sec",
            Duration = "Variable",
            Color = "#E3F2FD",
            Rarity = 2,
            Multiplier = 3.0,
            Keywords = new[] { "snowy", "snow", "blizzard", "winter" }
        },
        [BiomeType.Rainy] = new BiomeMetadata
        {
            DisplayName = "Rainy",
            SpawnChance = "1/750/sec",
            Duration = "Variable",
            Color = "#42A5F5",
            Rarity = 2,
            Multiplier = 4.0,
            Keywords = new[] { "rainy", "rain", "storm" }
        },
        
        // Event Biomes
        [BiomeType.PumpkinMoon] = new BiomeMetadata
        {
            DisplayName = "Pumpkin Moon",
            SpawnChance = "Event",
            Duration = "Event",
            Color = "#FF6F00",
            Rarity = 7,
            Keywords = new[] { "pumpkin moon", "pumpkin", "halloween" }
        },
        [BiomeType.Graveyard] = new BiomeMetadata
        {
            DisplayName = "Graveyard",
            SpawnChance = "Event",
            Duration = "Event",
            Color = "#37474F",
            Rarity = 7,
            Keywords = new[] { "graveyard", "grave", "cemetery" }
        },
        [BiomeType.BloodRain] = new BiomeMetadata
        {
            DisplayName = "Blood Rain",
            SpawnChance = "Event",
            Duration = "Event",
            Color = "#B71C1C",
            Rarity = 8,
            Keywords = new[] { "blood rain", "bloodrain", "blood" }
        },
        [BiomeType.Aurora] = new BiomeMetadata
        {
            DisplayName = "Aurora",
            SpawnChance = "Event",
            Duration = "Event",
            Color = "#26C6DA",
            Rarity = 7,
            Keywords = new[] { "aurora", "northern lights", "borealis" }
        },
        
        [BiomeType.Unknown] = new BiomeMetadata
        {
            DisplayName = "Unknown",
            SpawnChance = "?",
            Duration = "?",
            Color = "#757575",
            Rarity = 0,
            Keywords = Array.Empty<string>()
        }
    };
    
    public static BiomeMetadata GetMetadata(BiomeType type) =>
        _biomes.TryGetValue(type, out var meta) ? meta : _biomes[BiomeType.Unknown];
    
    public static BiomeType ParseFromString(string text)
    {
        var lower = text.ToLowerInvariant().Trim();
        
        // 1. Try exact match first (most reliable)
        foreach (var (type, meta) in _biomes)
        {
            if (lower == meta.DisplayName.ToLowerInvariant())
                return type;
        }
        
        // 2. Build a list of all keywords with their biome types, sorted by length (longest first)
        var allKeywords = new List<(string keyword, BiomeType type)>();
        foreach (var (type, meta) in _biomes)
        {
            foreach (var kw in meta.Keywords)
            {
                allKeywords.Add((kw, type));
            }
        }
        
        // Sort by keyword length descending (longer matches first to avoid "dream" matching before "dreamspace")
        allKeywords.Sort((a, b) => b.keyword.Length.CompareTo(a.keyword.Length));
        
        // 3. Check for keyword matches (longest first)
        foreach (var (keyword, type) in allKeywords)
        {
            if (lower.Contains(keyword))
                return type;
        }
        
        return BiomeType.Unknown;
    }
    
    public static IEnumerable<BiomeType> AllTypes => _biomes.Keys;
}

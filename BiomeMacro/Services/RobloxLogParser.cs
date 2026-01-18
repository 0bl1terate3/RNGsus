using System;
using System.Text.RegularExpressions;
using BiomeMacro.Models;

namespace BiomeMacro.Services;

public class RobloxLogParser
{
    // Patterns for biome chat announcements in Sol's RNG
    private static readonly Regex[] BiomePatterns = new[]
    {
        // Common biome announcement patterns
        new Regex(@"(?:A|The)\s+(\w+(?:\s+\w+)?)\s+(?:has\s+)?(?:appeared|spawned|started|begun)", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        
        // Biome ended patterns
        new Regex(@"(?:The\s+)?(\w+(?:\s+\w+)?)\s+(?:has\s+)?(?:ended|disappeared|stopped|faded)", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        
        // Direct biome mentions (system messages)
        new Regex(@"\[System\].*?(?:biome|weather)[:\s]+(\w+(?:\s+\w+)?)", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        
        // Chat log format with biome keywords
        new Regex(@"(?:biome|weather)\s*(?:is\s+now|changed\s+to|:)\s*(\w+(?:\s+\w+)?)", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        
        // Sol's RNG specific patterns
        new Regex(@"(Sandstorm|Hell|Starfall|Heaven|Corruption|Null|Glitched|Dreamspace|Cyberspace|Windy|Snowy|Rainy|Pumpkin\s*Moon|Graveyard|Blood\s*Rain|Aurora)", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };
    
    // Pattern to detect chat messages in Roblox logs
    private static readonly Regex ChatLogPattern = new(
        @"(?:\[Chat\]|\[System\]|SendChat|ReceivedChat|OnMessage)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    
    public event Action<BiomeInfo>? OnBiomeDetected;
    public event Action<BiomeInfo>? OnBiomeEnded;
    public event Action<string>? OnParseError;
    
    private BiomeType _lastDetectedBiome = BiomeType.Normal;
    private DateTime _lastDetectionTime = DateTime.MinValue;
    
    // Debounce to prevent duplicate detections
    private static readonly TimeSpan DebounceTime = TimeSpan.FromSeconds(5);
    
    public void ParseLine(string line)
    {
        try
        {
            // Skip lines that are clearly not chat/game related
            if (string.IsNullOrWhiteSpace(line) || 
                line.StartsWith("FLog::") ||
                line.Contains("HttpResponse") ||
                line.Contains("RenderJob"))
            {
                return;
            }
            
            // Focus on chat-related log entries
            bool isChatRelated = ChatLogPattern.IsMatch(line);
            
            // Check for biome patterns
            foreach (var pattern in BiomePatterns)
            {
                var match = pattern.Match(line);
                if (match.Success)
                {
                    var biomeName = match.Groups[1].Value.Trim();
                    var biomeType = BiomeDatabase.ParseFromString(biomeName);
                    
                    if (biomeType == BiomeType.Unknown)
                        continue;
                    
                    // Debounce duplicate detections
                    if (biomeType == _lastDetectedBiome && 
                        DateTime.Now - _lastDetectionTime < DebounceTime)
                    {
                        continue;
                    }
                    
                    // Check if this is a biome end message
                    bool isEnding = line.ToLowerInvariant().Contains("ended") ||
                                   line.ToLowerInvariant().Contains("disappeared") ||
                                   line.ToLowerInvariant().Contains("stopped") ||
                                   line.ToLowerInvariant().Contains("faded");
                    
                    var biomeInfo = new BiomeInfo
                    {
                        Type = biomeType,
                        Name = BiomeDatabase.GetMetadata(biomeType).DisplayName,
                        DetectedAt = DateTime.Now,
                        Source = "Roblox"
                    };
                    
                    if (isEnding)
                    {
                        OnBiomeEnded?.Invoke(biomeInfo);
                        
                        // Return to normal biome
                        _lastDetectedBiome = BiomeType.Normal;
                        _lastDetectionTime = DateTime.Now;
                        
                        var normalBiome = new BiomeInfo
                        {
                            Type = BiomeType.Normal,
                            Name = "Normal",
                            DetectedAt = DateTime.Now,
                            Source = "Roblox"
                        };
                        OnBiomeDetected?.Invoke(normalBiome);
                    }
                    else
                    {
                        _lastDetectedBiome = biomeType;
                        _lastDetectionTime = DateTime.Now;
                        OnBiomeDetected?.Invoke(biomeInfo);
                    }
                    
                    break; // Only trigger once per line
                }
            }
        }
        catch (Exception ex)
        {
            OnParseError?.Invoke($"Roblox parse error: {ex.Message}");
        }
    }
}

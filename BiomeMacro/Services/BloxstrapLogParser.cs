using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using BiomeMacro.Models;

namespace BiomeMacro.Services;

public class BloxstrapLogParser
{
    // Pattern to match BloxstrapRPC JSON messages
    private static readonly Regex RpcPattern = new(
        @"\[BloxstrapRPC\]\s*({.*})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    
    // Pattern for rich presence state/details containing biome info
    private static readonly Regex BiomeStatePattern = new(
        @"(?:biome|state|details)["":\s]+([^""]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    
    public event Action<BiomeInfo>? OnBiomeDetected;
    public event Action<string>? OnParseError;
    
    public void ParseLine(string line)
    {
        try
        {
            // Check for BloxstrapRPC marker
            var rpcMatch = RpcPattern.Match(line);
            if (!rpcMatch.Success)
                return;
            
            var jsonString = rpcMatch.Groups[1].Value;
            
            // Try to parse as JSON
            using var doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;
            
            // Look for biome information in various JSON structures
            string? biomeText = null;
            
            // Check common RPC payload structures
            if (root.TryGetProperty("data", out var dataElement))
            {
                biomeText = ExtractBiomeFromElement(dataElement);
            }
            
            if (biomeText == null && root.TryGetProperty("state", out var stateElement))
            {
                biomeText = stateElement.GetString();
            }
            
            if (biomeText == null && root.TryGetProperty("details", out var detailsElement))
            {
                biomeText = detailsElement.GetString();
            }
            
            if (biomeText == null && root.TryGetProperty("largeImage", out var imageElement))
            {
                // Sol's RNG often includes biome in image name
                var imageName = imageElement.TryGetProperty("key", out var keyEl) 
                    ? keyEl.GetString() 
                    : imageElement.GetString();
                    
                if (!string.IsNullOrEmpty(imageName))
                    biomeText = imageName;
            }
            
            // Also check the raw JSON string for biome keywords
            if (biomeText == null)
            {
                biomeText = jsonString;
            }
            
            if (!string.IsNullOrEmpty(biomeText))
            {
                var biomeType = BiomeDatabase.ParseFromString(biomeText);
                
                if (biomeType != BiomeType.Unknown)
                {
                    var biomeInfo = new BiomeInfo
                    {
                        Type = biomeType,
                        Name = BiomeDatabase.GetMetadata(biomeType).DisplayName,
                        DetectedAt = DateTime.Now,
                        Source = "Bloxstrap"
                    };
                    
                    OnBiomeDetected?.Invoke(biomeInfo);
                }
            }
        }
        catch (JsonException)
        {
            // Not valid JSON, might be partial or malformed
        }
        catch (Exception ex)
        {
            OnParseError?.Invoke($"Bloxstrap parse error: {ex.Message}");
        }
    }
    
    private static string? ExtractBiomeFromElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();
        
        if (element.ValueKind == JsonValueKind.Object)
        {
            // Try common property names
            foreach (var prop in new[] { "biome", "currentBiome", "state", "details", "name" })
            {
                if (element.TryGetProperty(prop, out var propElement) && 
                    propElement.ValueKind == JsonValueKind.String)
                {
                    return propElement.GetString();
                }
            }
        }
        
        return null;
    }
}

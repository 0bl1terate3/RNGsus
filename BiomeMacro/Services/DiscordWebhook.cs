using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BiomeMacro.Models;

namespace BiomeMacro.Services;

public class DiscordWebhook : IDisposable
{
    private readonly HttpClient _httpClient;
    private string? _webhookUrl;
    
    public bool IsEnabled => !string.IsNullOrWhiteSpace(_webhookUrl);
    
    public event Action<string>? OnError;
    public event Action<string>? OnSuccess;
    
    public DiscordWebhook()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "BiomeMacro/1.0");
    }
    
    public void SetWebhookUrl(string? url)
    {
        _webhookUrl = url;
    }
    
    public async Task SendBiomeNotificationAsync(BiomeInfo biome, byte[]? screenshot = null)
    {
        if (!IsEnabled || _webhookUrl == null)
            return;
        
        try
        {
            var metadata = biome.Metadata;
            
            // Clean format (No emojis in text)
            var colorHex = metadata.Color.TrimStart('#');
            var colorDecimal = Convert.ToInt32(colorHex, 16);
            
            // Fields array for cleaner layout
            var fields = new[]
            {
                new { name = "Biome", value = biome.Name, inline = true },
                new { name = "Chance", value = metadata.SpawnChance, inline = true },
                new { name = "Rarity", value = $"1 in {metadata.Rarity}", inline = true },
                new { name = "Instance", value = biome.Source, inline = true },
                new { name = "Time", value = biome.DetectedAt.ToString("HH:mm:ss"), inline = true }
            };

            var embed = new
            {
                title = "Biome Detected",
                color = colorDecimal,
                fields = fields,
                footer = new { text = "Biome Macro Tracker" },
                timestamp = DateTime.UtcNow.ToString("o"),
                image = screenshot != null ? new { url = "attachment://screenshot.png" } : null
            };
            
            await SendExAsync(embed, screenshot, "screenshot.png");
            OnSuccess?.Invoke($"Sent notification for {biome.Name}");
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Webhook error: {ex.Message}");
        }
    }
    
    public async Task SendCustomAlertAsync(string title, string instance, string details, int color, byte[]? screenshot = null)
    {
        if (!IsEnabled || _webhookUrl == null) return;
        
        try
        {
            var embed = new
            {
                title = title,
                color = color,
                fields = new[]
                {
                    new { name = "Instance", value = instance, inline = true },
                    new { name = "Details", value = details, inline = true },
                    new { name = "Time", value = DateTime.Now.ToString("HH:mm:ss"), inline = true }
                },
                footer = new { text = "Biome Macro Tracker" },
                timestamp = DateTime.UtcNow.ToString("o"),
                image = screenshot != null ? new { url = "attachment://screenshot.png" } : null
            };
            
            await SendExAsync(embed, screenshot, "screenshot.png");
            OnSuccess?.Invoke($"Sent alert: {title}");
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Alert error: {ex.Message}");
        }
    }

    private async Task SendExAsync(object embed, byte[]? fileData, string fileName)
    {
        using var content = new MultipartFormDataContent();
        
        var payload = new { embeds = new[] { embed } };
        var json = JsonSerializer.Serialize(payload);
        content.Add(new StringContent(json, Encoding.UTF8, "application/json"), "payload_json");

        if (fileData != null)
        {
            content.Add(new ByteArrayContent(fileData), "file", fileName);
        }

        var response = await _httpClient.PostAsync(_webhookUrl, content);
        
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Failed with {response.StatusCode}");
        }
    }
    
    public async Task SendTestMessageAsync()
    {
        // Simple test without screenshot
        await SendCustomAlertAsync("Webhook Connected", "System", "Your webhook is working correctly.", 0x57F287);
    }
    
    // Legacy support or redirects
    public async Task SendCustomMessageAsync(string title, string message, int color = 0x5865F2)
    {
        await SendCustomAlertAsync(title, "System", message, color);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

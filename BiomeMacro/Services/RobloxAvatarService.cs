using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BiomeMacro.Services;

/// <summary>
/// Fetches Roblox avatar thumbnails using the Roblox API.
/// </summary>
public class RobloxAvatarService : IDisposable
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, (long UserId, string? AvatarUrl)> _cache = new(StringComparer.OrdinalIgnoreCase);
    
    public RobloxAvatarService()
    {
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "BiomeMacro/1.0");
    }
    
    /// <summary>
    /// Get the avatar headshot URL for a Roblox username.
    /// </summary>
    public async Task<string?> GetAvatarUrlAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;
        
        // Check cache first
        if (_cache.TryGetValue(username, out var cached) && cached.AvatarUrl != null)
            return cached.AvatarUrl;
        
        try
        {
            // Step 1: Get user ID from username
            var userId = await GetUserIdAsync(username);
            if (userId == null)
                return null;
            
            // Step 2: Get avatar thumbnail
            var avatarUrl = await GetThumbnailAsync(userId.Value);
            
            // Cache the result
            _cache[username] = (userId.Value, avatarUrl);
            
            return avatarUrl;
        }
        catch
        {
            return null;
        }
    }
    
    private async Task<long?> GetUserIdAsync(string username)
    {
        // Check cache for user ID
        if (_cache.TryGetValue(username, out var cached))
            return cached.UserId;
        
        try
        {
            var requestBody = new { usernames = new[] { username }, excludeBannedUsers = true };
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            
            var response = await _http.PostAsync(
                "https://users.roblox.com/v1/usernames/users",
                content
            );
            
            if (!response.IsSuccessStatusCode)
                return null;
            
            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0)
                return null;
            
            var userId = data[0].GetProperty("id").GetInt64();
            return userId;
        }
        catch
        {
            return null;
        }
    }
    
    private async Task<string?> GetThumbnailAsync(long userId)
    {
        try
        {
            var url = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png&isCircular=false";
            
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;
            
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() == 0)
                return null;
            
            var state = data[0].GetProperty("state").GetString();
            if (state != "Completed")
                return null;
            
            return data[0].GetProperty("imageUrl").GetString();
        }
        catch
        {
            return null;
        }
    }
    
    public void Dispose()
    {
        _http.Dispose();
    }
}

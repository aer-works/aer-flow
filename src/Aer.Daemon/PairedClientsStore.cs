using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aer.Daemon;

public class PairedClient
{
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("tokenHash")]
    public string TokenHash { get; set; } = string.Empty;

    [JsonPropertyName("pairedAt")]
    public DateTime PairedAt { get; set; }
}

public class PairedClientsData
{
    [JsonPropertyName("clients")]
    public List<PairedClient> Clients { get; set; } = new();
}

public class PairedClientsStore
{
    private readonly string _filePath;
    private readonly object _lock = new();

    public PairedClientsStore()
    {
        var aerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aer");
        _filePath = Path.Combine(aerDir, "paired_clients.json");
    }

    public PairedClientsStore(string filePath)
    {
        _filePath = filePath;
    }

    public PairedClientsData Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                return new PairedClientsData();
            }

            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<PairedClientsData>(json) ?? new PairedClientsData();
            }
            catch
            {
                return new PairedClientsData();
            }
        }
    }

    public void Save(PairedClientsData data)
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);

            if (!OperatingSystem.IsWindows() && File.Exists(_filePath))
            {
                File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    public string AddClient(string name)
    {
        var rawToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var tokenHash = HashToken(rawToken);

        var data = Load();
        data.Clients.Add(new PairedClient
        {
            ClientId = Guid.NewGuid().ToString("D"),
            Name = name,
            TokenHash = tokenHash,
            PairedAt = DateTime.UtcNow
        });
        Save(data);

        return rawToken;
    }

    public bool ValidateToken(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken)) return false;

        var tokenHash = HashToken(rawToken);
        var data = Load();

        foreach (var client in data.Clients)
        {
            if (FixedTimeEquals(client.TokenHash, tokenHash))
            {
                return true;
            }
        }

        return false;
    }

    private static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}

public static class PairingCodeManager
{
    private static readonly object _lock = new();
    private static string? _activeCode;
    private static DateTime _expiry;

    public static string GenerateCode()
    {
        lock (_lock)
        {
            var random = new Random();
            _activeCode = random.Next(100000, 999999).ToString();
            _expiry = DateTime.UtcNow.AddSeconds(60);
            return _activeCode;
        }
    }

    public static bool ValidateAndConsume(string code)
    {
        lock (_lock)
        {
            if (_activeCode != null && _activeCode == code && DateTime.UtcNow < _expiry)
            {
                _activeCode = null; // Consume
                return true;
            }
            return false;
        }
    }
}

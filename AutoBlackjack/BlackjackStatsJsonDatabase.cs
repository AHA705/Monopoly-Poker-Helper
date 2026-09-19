using BepInEx;
using BepInEx.Unity.IL2CPP;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoBlackjack;

/// <summary>
/// Manages JSON database for tracking blackjack statistics.
/// Tracks: bet amount, date/time, chip balance, and round outcome.
/// 
/// Logger doesnt work
/// </summary>
public class BlackjackStatsJsonDatabase : IDisposable
{
    private readonly string _jsonPath;
    private List<BlackjackRoundRecord> _records;
    private bool _disposed;

    public BlackjackStatsJsonDatabase(string jsonPath)
    {
        _jsonPath = jsonPath;
        Initialize();
    }

    private void Initialize()
    {
        try
        {
            // Ensure directory exists
            string directory = Path.GetDirectoryName(_jsonPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Load existing records or create new list
            if (File.Exists(_jsonPath))
            {
                string jsonContent = File.ReadAllText(_jsonPath);
                _records = JsonSerializer.Deserialize<List<BlackjackRoundRecord>>(jsonContent)
                            ?? new List<BlackjackRoundRecord>();
                //Log.LogInfo($"Loaded {_records.Count} records from JSON database");
            }
            else
            {
                _records = new List<BlackjackRoundRecord>();
                SaveToFile();
                //Log.LogInfo($"Created new JSON database at: {_jsonPath}");
            }
        }
        catch (Exception ex)
        {
            //Log.Error($"Failed to initialize JSON database: {ex}");
            _records = new List<BlackjackRoundRecord>();
        }
    }

    /// <summary>
    /// Log a blackjack round with statistics.
    /// </summary>
    public void LogRound(long betAmount, long chipBalance, string outcome = null,
                            int playerTotal = 0, int dealerUpcard = 0, string action = null, long profit = 0)
    {
        try
        {
            var record = new BlackjackRoundRecord
            {
                Id = _records.Count > 0 ? _records[^1].Id + 1 : 1,
                Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                BetAmount = betAmount,
                ChipBalance = chipBalance,
                Outcome = outcome ?? "",
                PlayerTotal = playerTotal,
                DealerUpcard = dealerUpcard,
                Action = action ?? "",
                Profit = profit
            };

            _records.Add(record);
            SaveToFile();
        }
        catch (Exception ex)
        {
            //Log.Error($"Failed to log round to JSON database: {ex}");
        }
    }

    /// <summary>
    /// Save records to JSON file.
    /// </summary>
    private void SaveToFile()
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            string jsonContent = JsonSerializer.Serialize(_records, options);
            File.WriteAllText(_jsonPath, jsonContent);
        }
        catch (Exception ex)
        {
            //Log.Error($"Failed to save JSON database: {ex}");
        }
    }

    /// <summary>
    /// Get total number of rounds played.
    /// </summary>
    public long GetTotalRounds()
    {
        return _records?.Count ?? 0;
    }

    /// <summary>
    /// Get total profit/loss across all rounds.
    /// </summary>
    public long GetTotalProfit()
    {
        try
        {
            if (_records == null || _records.Count == 0)
                return 0;

            long total = 0;
            foreach (var record in _records)
            {
                total += record.Profit;
            }
            return total;
        }
        catch (Exception ex)
        {
            //Log.Error($"Failed to get total profit: {ex}");
            return 0;
        }
    }

    /// <summary>
    /// Get statistics summary.
    /// </summary>
    public void LogStatsSummary()
    {
        try
        {
            long totalRounds = GetTotalRounds();
            long totalProfit = GetTotalProfit();

            //Log.Info("=== Blackjack Stats Summary (JSON) ===");
            //Log.Info($"Total Rounds: {totalRounds}");
            //Log.Info($"Total Profit: {totalProfit:+#;-#;0} chips");
            //if (totalRounds > 0)
                //Log.Info($"Average Profit per Round: {(double)totalProfit / totalRounds:F2} chips");
            //Log.Info("=======================================");
        }
        catch (Exception ex)
        {
            //Log.Error($"Failed to log stats summary: {ex}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            SaveToFile();
        }
        catch (Exception ex)
        {
            //Log.Error($"Error disposing JSON database: {ex}");
        }

        _disposed = true;
    }
}

/// <summary>
/// Represents a single blackjack round record.
/// </summary>
public class BlackjackRoundRecord
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; }

    [JsonPropertyName("betAmount")]
    public long BetAmount { get; set; }

    [JsonPropertyName("chipBalance")]
    public long ChipBalance { get; set; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; }

    [JsonPropertyName("playerTotal")]
    public int PlayerTotal { get; set; }

    [JsonPropertyName("dealerUpcard")]
    public int DealerUpcard { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; }

    [JsonPropertyName("profit")]
    public long Profit { get; set; }
}
using BepInEx.Logging;

namespace MonopolyPokerHelper;

internal static class LoggerHelper
{
    internal static ManualLogSource Log { get; set; }
    internal static void LogInfo(string message) => Log?.LogInfo(message);
    internal static void LogWarn(string message) => Log?.LogWarning(message);
    internal static void LogError(string message) => Log?.LogError(message);
    internal static void LogDebug(string message) => Log?.LogDebug(message);
}
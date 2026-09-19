using System;
using System.Reflection;

using static MonopolyPokerHelper.LoggerHelper;

namespace MonopolyPokerHelper;

/// <summary>
/// BepInEx 6 IL2CPP plugin that automatically claims Boardwalk free dice rolls in Monopoly Poker.
/// 
/// - Boardwalk Roll       — Dice roll whenever available, usually available every ~90 - 180 minutes
///
/// Works by hooking SocialLayerManager.Update() and running periodic checks
/// via reflection (no compile-time game assembly references needed).
/// </summary>
internal static class BoardwalkHandler
{
    // Cooldown flags (avoid spamming the same action)
    private static bool _boardwalkRolled;
    private static float _boardwalkRetryTimer;
    private const float BOARDWALK_RETRY_INTERVAL = 60f; // re-check boardwalk every 1 minute

    // BoardwalkManager cached reflection handles
    private static Type _boardwalkMgrType;
    private static PropertyInfo _boardwalkInstance;
    private static PropertyInfo _canRollDice;
    private static MethodInfo _doDiceRoll;
    public static bool IsResolved { get; private set; }

    public static void ResolveReflection()
    {
        if (IsResolved) return;
        try
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;

            _boardwalkMgrType = ReflectionHelper.FindType("BoardwalkManager");
            if (_boardwalkMgrType != null)
            {
                _boardwalkInstance = _boardwalkMgrType.GetProperty("Instance", all);
                _canRollDice = _boardwalkMgrType.GetProperty("CanRollDice", all);
                _doDiceRoll = _boardwalkMgrType.GetMethod("DoDiceRoll", all);
                LogDebug($"BoardwalkManager: found={_boardwalkMgrType != null} " +
                        $"instance={_boardwalkInstance != null} " +
                        $"canRoll={_canRollDice != null} " +
                        $"doRoll={_doDiceRoll != null}");
            }
            else
            {
                LogError("BoardwalkManager type not found!");
                throw new TypeLoadException(
                    "BoardwalkHandler failed to resolve required type 'BoardwalkManager'.");
            }

            IsResolved = true;
            LogInfo("Reflection resolution complete.");
        }
        catch (Exception ex)
        {
            LogError($"Failed to resolve BoardwalkManager reflection: {ex}");
        }
    }

    public static void Update(float deltaTime)
    {
        if (!IsResolved) return;

        _boardwalkRetryTimer += deltaTime;

        if (_boardwalkRolled && _boardwalkRetryTimer < BOARDWALK_RETRY_INTERVAL)
            return;

        TryBoardwalkRoll();
    }

    private static void TryBoardwalkRoll()
    {
        try
        {
            var bwInstance = _boardwalkInstance?.GetValue(null);
            if (bwInstance == null) { Log.LogError("BoardwalkManager instance is null"); return; }

            var canRoll = _canRollDice?.GetValue(bwInstance);
            if (canRoll == null || !Convert.ToBoolean(canRoll))
            {
                LogDebug("Boardwalk dice roll not available yet.");
                return;
            }

            _doDiceRoll.Invoke(bwInstance, [false]);
            _boardwalkRolled = true;
            _boardwalkRetryTimer = 0f;
            LogInfo(">>> Rolled Boardwalk dice!");
        }
        catch (Exception ex)
        {
            LogError($"Error with Boardwalk roll: {ex.Message}");
        }
    }

    public static void Reset()
    {
        _boardwalkRolled = false;
        _boardwalkRetryTimer = 0f;
    }
}
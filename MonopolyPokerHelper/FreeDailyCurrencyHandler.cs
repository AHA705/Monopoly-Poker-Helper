using System;
using System.Reflection;

using static MonopolyPokerHelper.LoggerHelper;

namespace MonopolyPokerHelper;

internal static class FreeDailyCurrencyHandler
{
    // FreeBonusManager
    private static Type _freeBonusMgrType;
    private static PropertyInfo _freeBonusInstance;
    private static PropertyInfo _canClaimFreeBonus;
    private static MethodInfo _collectBonus;

    public static bool IsResolved { get; private set; }
    
    public static void ResolveReflection()
    {
        try
        {
            if (IsResolved)
            {
                LogInfo("FreeDailyCurrencyHandler reflection already resolved.");
                return;
            }
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                        BindingFlags.Instance | BindingFlags.Static;

            _freeBonusMgrType = ReflectionHelper.FindType("FreeBonusManager");
            if (_freeBonusMgrType != null)
            {
                _freeBonusInstance = _freeBonusMgrType.GetProperty("Instance", all)
                                    ?? _freeBonusMgrType.BaseType?.GetProperty("Instance", all);
                _canClaimFreeBonus = _freeBonusMgrType.GetProperty("CanClaimFreeBonus", all);
                _collectBonus = _freeBonusMgrType.GetMethod("CollectBonus", all);

                LogInfo($"FreeBonusManager: found={_freeBonusMgrType != null} " +
                            $"instance={_freeBonusInstance != null} " +
                            $"canClaim={_canClaimFreeBonus != null} " +
                            $"collect={_collectBonus != null}");

                IsResolved = true;
            }
            else
            {
                LogError("FreeBonusManager type not found!");
                throw new TypeLoadException("FreeDailyCurrencyHandler failed to resolve required type 'FreeBonusManager'.");
            }
        }
        catch (Exception ex)
        {
            LogError($"Failed to resolve FreeBonusManager reflection: {ex}");
        }
    }

    public static void Update()
    {
        if (!IsResolved) return;

        TryCollectFreeBonus(0, "Chips");
        TryCollectFreeBonus(1, "M-Money");
    }

    private static void TryCollectFreeBonus(int channel, string label)
    {
        try
        {
            if (_freeBonusMgrType == null || _freeBonusInstance == null)
            {
                throw new InvalidOperationException($"FreeBonusManager type or instance property not found, cannot claim free {label}.");
            }
            

            var instance = _freeBonusInstance.GetValue(null);
            if (instance == null) { LogError($"FreeBonusManager instance is null"); return; }

            var canClaim = _canClaimFreeBonus?.GetValue(instance);
            if (canClaim == null) { LogError($"canClaimFreeBonus instance is null") ; return; }

            bool canClaimChannel;
            if (canClaim is bool[] boolArr)
            {
                if (channel >= boolArr.Length) return;
                canClaimChannel = boolArr[channel];
            }
            else
            {
                var getMethod = canClaim.GetType().GetMethod("get_Item")
                                ?? canClaim.GetType().GetMethod("Get");
                    if (getMethod != null)
                    {
                        var result = getMethod.Invoke(canClaim, new object[] { channel });
                        canClaimChannel = Convert.ToBoolean(result);
                    }
                else
                {
                        var indexer = canClaim.GetType().GetProperty("Item");
                        if (indexer != null)
                        {
                            canClaimChannel = Convert.ToBoolean(indexer.GetValue(canClaim, new object[] { channel }));
                        }
                    else
                    {
                        LogError($"Could not read CanClaimFreeBonus[{channel}]");
                        return;
                    }
                }
            }

            if (!canClaimChannel)
            {
                LogDebug($"Free {label} not available (already claimed or on cooldown).");
                return;
            }

            _collectBonus.Invoke(instance, new object[] { channel });
            LogInfo($">>> Claimed free {label} (channel {channel})!");
        }
        catch (Exception ex)
        {
            LogError($"Error collecting free {label}: {ex.Message}");
        }
    }

    public static void Reset()
    {
        //_chipsClaimed = false;
        //_mMoneyClaimed = false;
    }
}

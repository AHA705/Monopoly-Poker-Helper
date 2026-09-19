using System;
using System.Reflection;
using static MonopolyPokerHelper.LoggerHelper;

namespace MonopolyPokerHelper
{
    //<summary>
    // Currently only handles auto-clicking the bailout button when the bankruptcy popup appears
    // but can be expanded in the future for doing it before the popup appears, or for other related features.
    //</summary>
    internal static class BankrupcyRefillHandler
    {
        internal static void Load()
        {
            var postfix = typeof(BankrupcyRefillHandler).GetMethod(
                nameof(AutoClickBailout), BindingFlags.Static | BindingFlags.NonPublic) 
                ?? throw new Exception($"{MethodBase.GetCurrentMethod()?.Name} - BankrupcyRefillHandler.AutoClickBailout method not found.");

            var harmonyPostfix = new HarmonyLib.HarmonyMethod(postfix);

            // Hook into the popup showing up
            Plugin.PatchMethod("YoudaGames.MonopolyPoker.Popups.BailoutPopup", "Show", harmonyPostfix, true);
            LogInfo("Hooked into BailoutPopup.Show for auto-refill.");
        }

        private static void AutoClickBailout(object __instance)
        {
            LogInfo("BailoutPopup appeared! Automatically clicking bailout...");

            try
            {
                // Find and invoke the BailoutClicked method on the given instance
                var clickMethod = __instance.GetType().GetMethod("BailoutClicked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (clickMethod != null)
                {
                    clickMethod.Invoke(__instance, null);
                    LogInfo("Successfully auto-clicked the Bailout button.");
                }
                else
                {
                    LogError("BailoutClicked method could not be found via reflection.");
                }
            }
            catch (Exception ex)
            {
                LogError($"Failed to click bailout: {ex.Message}");
            }
        }
    }
}

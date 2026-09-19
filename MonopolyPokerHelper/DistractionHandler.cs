using System;
using System.Reflection;
using static MonopolyPokerHelper.LoggerHelper;

/// <summary>
/// Suppresses Distractions and Rolling Offers pop-ups.
///   </summary>
namespace MonopolyPokerHelper
{
    internal static class DistractionHandler
    {
        internal static void Load()
        {
            // Method Distractions
            var targetMethods = new (string TypeName, string MethodName)[]
            {
                // // Has daily free packs on login.
                //("YoudaGames.MonopolyPoker.Popups.RollingOffersPopup", "Show"),         // Rolling Offers Popup (the actual UI)
                //("YoudaGames.MonopolyPoker.RollingOffers.MPRollingOffersManager", "OpenPopup"), //Rolling Offers Manager (triggers the popup)
                //("YoudaGames.MonopolyPoker.HUD.RollingOffersHUDSideButton", "OnClick"),   // HUD Button that opens the Rolling Offers Popup

                 // Vault related classes - blocking all methods to prevent any vault-related popups, events, or UI from functioning
                ("YoudaGames.MonopolyPoker.UI.HUD.Buttons.VaultHUDButton", "Show"),

                // Game Boot popups
                ("YoudaGames.MonopolyPoker.Popups.BuyOneOrAllPopup", "Show"),
                ("YoudaGames.MonopolyPoker.Popups.DealPopup", "Show"),
                ("YoudaGames.MonopolyPoker.Popups.LimitedOffersPopup", "Show"),
            };

            var blockPatch = typeof(DistractionHandler).GetMethod(
                nameof(BlockPrefix), BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new Exception($"{MethodBase.GetCurrentMethod()?.Name} - DistractionHandler.BlockPrefix method not found.");

            foreach (var (TypeName, MethodName) in targetMethods)
            {
                Plugin.PatchMethod(TypeName, MethodName, blockPatch);
                LogInfo($"Blocked Type: {TypeName} Method: {MethodName}");
            }

            // GameObjects - for HUD elements that don't have a show method but are active by default
            var gameObjects = new (string TypeName, string MethodName)[]
            {
                // BuyOneOrAll doesn't declare its own Awake, so we patch its base class
                // ("YoudaGames.MonopolyPoker.UI.HUD.SideButtons.BuyOneOrAllHUDSideButton", "Awake"), Doesn't have an awake method, patching the base
                ("YoudaGames.MonopolyPoker.UI.HUD.SideButtons.BaseLeftHUDSideButton", "Awake"),
                ("YoudaGames.MonopolyPoker.UI.HUD.SideButtons.DealsHUDSideButton", "Awake"),
            };

            var disableObjectPatch = typeof(DistractionHandler).GetMethod(
                nameof(DisableGameObjectPostfix), BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new Exception($"{MethodBase.GetCurrentMethod()?.Name} - DistractionHandler.DisableGameObjectPostfix method not found.");

            var disablePatchHarmony = new HarmonyLib.HarmonyMethod(disableObjectPatch);
            foreach (var (TypeName, MethodName) in gameObjects)
            {
                Plugin.PatchMethod(TypeName, MethodName, disablePatchHarmony, isPostfix: true);
                LogInfo($"Blocked GameObject: {TypeName}");
            }
        }

        /// <summary>
        /// Harmony prefix that blocks execution of the original method.
        /// Returning false = skip original method body entirely.
        /// Harmony redirects to here.
        /// </summary>
        private static bool BlockPrefix() => false;

        /// <summary>
        /// Forces a MonoBehaviour's GameObject to instantly deactivate.
        /// For HUD elements that don't have a show method but are active by default
        /// </summary>
        private static void DisableGameObjectPostfix(UnityEngine.Component __instance)
        {
            var name = __instance.GetType().Name;
            if (name is "BuyOneOrAllHUDSideButton" or "DealsHUDSideButton")
            {
                __instance.gameObject.SetActive(false);
                LogDebug($"Deactivated GameObject for: {name}");
            }
        }
    }
}

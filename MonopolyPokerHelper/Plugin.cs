using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System;
using System.Reflection;
using static MonopolyPokerHelper.LoggerHelper;

namespace MonopolyPokerHelper
{
    /// <summary>
    /// Main BepInEx plugin entry point for Monopoly Poker helper features.
    /// Sets up Harmony patches and periodically runs feature handlers.
    /// </summary>
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BasePlugin
    {
        private static Harmony _harmony;

        // Accumulated time since last handler run.
        private static float _elapsed;
        // How often to run handlers after startup, in seconds.
        private const float CHECK_INTERVAL = 0.25f;
        // Initial delay before any logic runs, in seconds.
        private const float INITIAL_DELAY = 15f;
        // Ensures the initial delay logic only runs once.
        private static bool _initialDelayDone;
        // Ensures reflection is resolved successfully before handlers execute.
        public static bool IsResolved { get; private set; }
        public static bool _pluginDisabled;
        /// <summary>
        /// BepInEx entry point. Sets up Harmony patches and logs plugin startup.
        /// </summary>
        public override void Load()
        {
            LoggerHelper.Log = base.Log;
            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);

            // Redundant, BepInEx does this automatically
            //LogInfo($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} loading...");

            var updatePatch = new HarmonyMethod(typeof(Plugin).GetMethod(
                nameof(UpdatePostfix), BindingFlags.Static | BindingFlags.NonPublic));
            PatchMethod("SocialLayerManager", "Update", updatePatch, isPostfix: true);

            DistractionHandler.Load();
            BankrupcyRefillHandler.Load();

            LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

        }
        /// <summary>
        /// Resolves all reflection-based handles used by handlers.
        /// Must succeed before periodic updates start running.
        /// </summary>
        private static bool ResolveReflection()
        {
            if (IsResolved) return true;
            LogInfo("Resolving reflection handles...");

            BoardwalkHandler.ResolveReflection();
            FreeDailyCurrencyHandler.ResolveReflection();
            // ClaimMissionHandler.ResolveReflection();

            IsResolved = BoardwalkHandler.IsResolved &&
                            FreeDailyCurrencyHandler.IsResolved
                            // ClaimMissionHandler.IsResolved
                            ;
            if (IsResolved) { LogInfo("Reflection handles for Monopoly Poker helper resolved successfully."); }
            return IsResolved;
        }

        /// <summary>
        /// Harmony postfix for SocialLayerManager.Update.
        /// Throttles execution, enforces an initial delay,
        /// ensures reflection is ready, then invokes feature handlers.
        /// </summary>
        private static void UpdatePostfix()
        {
            if (_pluginDisabled) return;

            float dt = UnityEngine.Time.deltaTime;

            if (!_initialDelayDone)
            {
                _elapsed += dt;
                if (_elapsed < INITIAL_DELAY) return;
                _initialDelayDone = true;
                _elapsed = CHECK_INTERVAL;
                LogInfo("Initial start delay complete");
            }

            if (!ResolveReflection())
            {
                DisablePlugin("Failed to resolve reflection handles");
                return;
            }

            _elapsed += dt;

            if (_elapsed < CHECK_INTERVAL) return;
            _elapsed = 0f;

            try
            {
                //LogInfo("Running periodic handlers...");
                SafeInvoke(() => BoardwalkHandler.Update(dt), nameof(BoardwalkHandler.Update));
                SafeInvoke(FreeDailyCurrencyHandler.Update, nameof(FreeDailyCurrencyHandler.Update));
                // SafeInvoke(() => ClaimMissionHandler.Update(dt), nameof(ClaimMissionHandler.Update));
            }
            catch (Exception ex)
            {
                LogError($"UpdatePostfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Safely invokes a handler action and logs any exceptions with the given name.
        /// Use when you want individual handler failures not to affect others.
        /// </summary>
        private static void SafeInvoke(Action action, string name)
        {
            try { action(); }
            catch (Exception ex) { LogError($"Error invoking handler {name}: {ex}"); }
        }

        /// <summary>
        /// Finds a target type and patches all methods with the given name
        /// using the provided HarmonyMethod as prefix or postfix.
        /// </summary>
        /// <param name="typeName">Name of the target type to search for.</param>
        /// <param name="methodName">Name of the methods to patch on that type.</param>
        /// <param name="patch">Harmony patch method (prefix or postfix).</param>
        /// <param name="isPostfix">True to apply as postfix, false for prefix.</param>
        public static bool PatchMethod(
            string typeName,
            string methodName,
            HarmonyMethod patch,
            bool isPostfix = false,
            Type[] parameterTypes = null)
        {
            Type targetType = ReflectionHelper.FindType(typeName);
            if (targetType == null)
            {
                LogError($"  Type not found: {typeName}");
                return false;
            }

            int patched = 0;
            if (parameterTypes != null)
            {
                var method = targetType.GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    null,
                    parameterTypes,
                    null);
                if (method == null)
                {
                    LogError($"No method named '{methodName}' with specified parameters found on {typeName}");
                    return false;
                }
                if (isPostfix) _harmony.Patch(method, postfix: patch);
                else _harmony.Patch(method, prefix: patch);
                patched = 1;
            }
            else
            {
                var methods = targetType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.DeclaredOnly);

                foreach (var method in methods)
                {
                    if (method.Name != methodName) continue;
                    if (isPostfix) _harmony.Patch(method, postfix: patch);
                    else _harmony.Patch(method, prefix: patch);

                    patched++;
                }
            }

            if (patched == 0)
            {
                LogError($"No methods named '{methodName}' found on {typeName}");
                return false;
            }

            LogInfo($"Patched {patched} method(s): {typeName}.{methodName}");
            return true;
        }

        public static void PatchMethod(string typeName, string methodName, MethodInfo patchMethod, bool isPostfix = false)
        {
            if (patchMethod == null)
            {
                LogWarn($"Patch method is null for {typeName}.{methodName}");
                return;
            }

            PatchMethod(typeName, methodName, new HarmonyMethod(patchMethod), isPostfix);
        }
        private static void DisablePlugin(string reason)
        {
            if (_pluginDisabled) return;

            _pluginDisabled = true;
            LogError($"Disabling plugin: {reason}");
            try
            {
                _harmony?.UnpatchSelf();
            }
            catch (Exception e)
            {
                LogError($"Error during plugin unpatching: {e}");
            }
        }
    }
}
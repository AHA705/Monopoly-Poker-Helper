using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;
using static Il2CppSystem.Reflection.RuntimePropertyInfo;

namespace AutoBlackjack;

/// <summary>
/// BepInEx 6 IL2CPP plugin that automatically plays Blackjack in Monopoly Poker.
/// 
/// BlackjackCardValue enum (confirmed from runtime dump):
///   Two=0, Three=1, Four=2, Five=3, Six=4, Seven=5,
///   Eight=6, Nine=7, Ten=8, Jack=9, Queen=10, King=11, Ace=12, Unknown=-1
///
/// Round flow (confirmed from BlackjackView method dump):
///   ShowBlackjackBetInterface -> auto-bet fires here
///   HandleOnInitRound         -> initial deal: dealer upcard + player cards  Hook 2
///   HandleOnNextDecision      -> player decision                             Hook 3
///   HandleOnDealerCards       -> dealer reveals full hand (AFTER player acts)
///   HandleOnEndRound          -> results
///
/// Dealer upcard data path (confirmed from runtime dumps):
///   BlackjackRoundData.get_dealerHand() -> BlackjackHandData
///     .get_handValuesPerCard()[upcardIdx] -> List<int>  (possible values for that card)
///     max(inner list) -> direct blackjack point value (2-11, where 11 = Ace)
///
/// Note: get_Value() on BlackjackHandData cards always returns 0.
///       Those are data-layer objects; actual values live in handValuesPerCard.
///
/// Hooks:
///   1. BlackjackDecisionView.ShowBlackjackBetInterface -> auto-bet minimum
///   2. BlackjackView.HandleOnInitRound                 -> store dealer upcard
///   3. BlackjackView.HandleOnNextDecision              -> auto-decide hit/stand
/// </summary>
[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGUID = "com.mod.autoblackjack";
    public const string PluginName = "Auto Blackjack";
    public const string PluginVersion = "1.0.0";
    public const string GameVersion = "0.0"; // for future compatibility checks

    public static bool DEBUG = true; // set to false to suppress diagnostic logs
    public static bool START_BETTING = false;

    private Harmony _harmony;
    private static BepInEx.Logging.ManualLogSource _log;
    private static bool _pluginEnabled = true; // Runtime enable/disable flag
    private static BlackjackStatsJsonDatabase _statsDatabase; // JSON stats tracker

    // ── Reflection handles: shared ──────────────────────────────
    private static bool _sharedResolved;
    private static MethodInfo _blackjackAction;     // BlackjackEvents.BlackjackAction(String, Int32, Int64)
    private static MethodInfo _getActionBet;        // BlackjackUserDecision.get_ActionBet()  [static]
    private static MethodInfo _getActionHit;        // BlackjackUserDecision.get_ActionHit()  [static]
    private static MethodInfo _getActionStand;      // BlackjackUserDecision.get_ActionStand() [static]
    private static MethodInfo _getActionDouble;     // BlackjackUserDecision.get_ActionDouble() [static]
    private static MethodInfo _getActionSplit;      // BlackjackUserDecision.get_ActionSplit() [static]

    // ── Reflection handles: bet hook ─────────────────────────────
    private static bool _betResolved;
    private static MethodInfo _getBets;             // BlackjackAskBetData.get_bets()
    private static MethodInfo _getDecViewEvents;    // BlackjackDecisionView.get_m_BlackjackEvents()
    private static MethodInfo _hideBetInterface;    // BlackjackDecisionView.HideBlackjackBetInterface()

    // ── Reflection handles: decision hook ───────────────────────
    private static bool _decResolved;
    // From BlackjackNextDecisionData
    private static MethodInfo _getDecHandId;        // get_handId()    -> Int32
    private static MethodInfo _getDecBetAmt;        // get_handValue() -> Int64 (chip bet amount)
    // From BlackjackView
    private static MethodInfo _getBjViewEvents;     // get_m_BlackjackEvents()
    private static MethodInfo _getHandViews;        // get_m_HandViews() -> List<BlackjackHandView>
    private static MethodInfo _getDecInterface;     // get_m_DecisionInterface() -> BlackjackDecisionView
    // From BlackjackDecisionView
    private static MethodInfo _hideDecision;        // HideBlackjackDecision()
    private static MethodInfo _getDecUIGameObject;  // get_m_BlackjackDecisionInterface() -> GameObject
    // From BlackjackHandView
    private static MethodInfo _getHVHandId;         // get_HandId() -> Int32
    private static MethodInfo _getHVCards;          // get_m_Cards() -> List<BlackjackCardData>
    // From BlackjackCardData
    private static MethodInfo _getCardValue;        // get_Value() -> BlackjackCardValue enum

    // ── Reflection handles: dealer upcard (init-round hook) ─────
    private static bool _dealerResolved;
    private static MethodInfo _getRoundDealerHand;    // BlackjackRoundData.get_dealerHand()          -> BlackjackHandData
    private static MethodInfo _getHandValuesPerCard;  // BlackjackHandData.get_handValuesPerCard()    -> List<List<int>>
    private static MethodInfo _getHandValues;         // BlackjackHandData.get_handValues()           -> List<int>  (fallback)

    // ── Reflection handles: end round hook ──────────────────────
    private static bool _endRoundResolved;
    private static MethodInfo _getEndRoundData;       // BlackjackEndRoundData.get_roundResults()     -> List<BlackjackRoundResult>
    private static MethodInfo _getRoundResultHandId;  // BlackjackRoundResult.get_handId()            -> Int32
    private static MethodInfo _getRoundResultOutcome; // BlackjackRoundResult.get_outcome()           -> String or enum
    private static MethodInfo _getRoundResultChips;   // BlackjackRoundResult.get_chips()             -> Int64
    private static MethodInfo _getPlayerChips;        // Try to find player chip balance getter

    // ── Round state tracking ─────────────────────────────────────
    // Direct blackjack point value (2-11 where 11=Ace), -1 = not yet received.
    // Set in OnInitRound; read in OnHandleNextDecision.
    private static int _dealerUpcardValue = -1;
    private static long _roundStartChips = 0;         // Chip balance at round start
    private static long _roundBetAmount = 0;          // Total bet amount for the round
    private static int _roundPlayerTotal = 0;         // Player hand total (first hand if split)
    private static string _roundAction = "";          // Last action taken
    // ── Session tracking ─────────────────────────────────────────
    private static long _sessionNetProfit = 0;        // Cumulative net profit for entire session
    private static int _sessionRoundsPlayed = 0;      // Total rounds played this session

    public static int bet_index = 1;
    public override void Load()
    {
        _harmony = new Harmony(PluginGUID);

        // Initialize static logger for use from static methods
        _log = Log;

        Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

        // Initialize JSON stats database
        string dbPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
            "blackjack_stats.json");
        _statsDatabase = new BlackjackStatsJsonDatabase(dbPath);
        Log.LogInfo($"Stats database initialized at: {dbPath}");

        var patchTargets = new (string typeName, string methodName, string handler, bool isPostfix)[]
        {
            ("YoudaGames.MonopolyPoker.Games.View.BlackjackDecisionView", "ShowBlackjackBetInterface", nameof(OnShowBetInterface), true),
            ("YoudaGames.MonopolyPoker.Games.View.BlackjackView", "HandleOnInitRound", nameof(OnInitRound), true),
            ("YoudaGames.MonopolyPoker.Games.View.BlackjackView", "HandleOnNextDecision", nameof(OnHandleNextDecision), true),
            ("YoudaGames.MonopolyPoker.Games.View.BlackjackView", "HandleOnEndRound", nameof(OnEndRound), true)
        };

        foreach (var (typeName, methodName, handler, isPostfix) in patchTargets)
        {
            PatchMethod(
                typeName,
                methodName,
                new HarmonyMethod(typeof(Plugin).GetMethod(handler, BindingFlags.Static | BindingFlags.NonPublic)),
                isPostfix: isPostfix);
        }

        Log.LogInfo($"{PluginName} loaded.");

    }

    public override bool Unload()
    {
        try
        {
            // Log final session summary
            if (_sessionRoundsPlayed > 0)
            {
                Log.LogInfo("-------------------------------------------------------");
                Log.LogInfo($"SESSION SUMMARY:");
                Log.LogInfo($"  Rounds played: {_sessionRoundsPlayed}");
                Log.LogInfo($"  NET profit: {_sessionNetProfit:+#;-#;0} chips");
                if (_sessionRoundsPlayed > 0)
                {
                    double avgProfit = (double)_sessionNetProfit / _sessionRoundsPlayed;
                    Log.LogInfo($"  Average profit per round: {avgProfit:+0.00;-0.00;0.00} chips");
                }
                Log.LogInfo("-------------------------------------------------------");
            }

            // Log final stats summary
            _statsDatabase?.LogStatsSummary();

            // Cleanup
            _statsDatabase?.Dispose();
            _harmony?.UnpatchSelf();

            Log.LogInfo($"{PluginName} unloaded.");
            return true;
        }
        catch (Exception ex)
        {
            Log.LogError($"Error during unload: {ex}");
            return false;
        }
    }

    // ---------------------------------------------------------------
    //  Reflection resolvers
    // ---------------------------------------------------------------

    private static bool ResolveShared()
    {
        if (_sharedResolved) return true;
        try
        {
            var bindAll = BindingFlags.Public | BindingFlags.NonPublic |
                          BindingFlags.Instance | BindingFlags.Static;

            var eventsType = FindType("YoudaGames.MonopolyPoker.Games.BlackjackEvents");
            var userDecisionType = FindType("YoudaGames.MonopolyPoker.Games.Data.BlackjackUserDecision");

            _blackjackAction = eventsType?.GetMethod("BlackjackAction", bindAll);
            _getActionBet = userDecisionType?.GetMethod("get_ActionBet", bindAll);
            _getActionHit = userDecisionType?.GetMethod("get_ActionHit", bindAll);
            _getActionStand = userDecisionType?.GetMethod("get_ActionStand", bindAll);
            _getActionDouble = userDecisionType?.GetMethod("get_ActionDouble", bindAll);
            _getActionSplit = userDecisionType?.GetMethod("get_ActionSplit", bindAll);

            _sharedResolved = _blackjackAction != null && _getActionBet != null &&
                              _getActionHit != null && _getActionStand != null &&
                              _getActionDouble != null && _getActionSplit != null;

            if (!_sharedResolved)
            {
                _log.LogWarning("Shared resolve failed:");
                if (_blackjackAction == null) _log.LogWarning("  - BlackjackAction");
                if (_getActionBet == null) _log.LogWarning("  - get_ActionBet");
                if (_getActionHit == null) _log.LogWarning("  - get_ActionHit");
                if (_getActionStand == null) _log.LogWarning("  - get_ActionStand");
                if (_getActionDouble == null) _log.LogWarning("  - get_ActionDouble");
                if (_getActionSplit == null) _log.LogWarning("  - get_ActionSplit");
            }
            return _sharedResolved;
        }
        catch (Exception ex) { _log.LogError($"ResolveShared: {ex}"); return false; }
    }

    private static bool ResolveBet()
    {
        if (_betResolved) return true;
        if (!ResolveShared()) return false;
        try
        {
            var bindAll = BindingFlags.Public | BindingFlags.NonPublic |
                          BindingFlags.Instance | BindingFlags.Static;

            var askBetType = FindType("YoudaGames.MonopolyPoker.Games.Data.BlackjackAskBetData");
            var decViewType = FindType("YoudaGames.MonopolyPoker.Games.View.BlackjackDecisionView");

            _getBets = askBetType?.GetMethod("get_bets", bindAll);
            _getDecViewEvents = decViewType?.GetMethod("get_m_BlackjackEvents", bindAll);
            _hideBetInterface = decViewType?.GetMethod("HideBlackjackBetInterface", bindAll);

            _betResolved = _getBets != null && _getDecViewEvents != null;

            if (!_betResolved)
            {
                _log.LogWarning("Bet resolve failed:");
                if (_getBets == null) _log.LogWarning("  - get_bets");
                if (_getDecViewEvents == null) _log.LogWarning("  - DecisionView.get_m_BlackjackEvents");
            }
            return _betResolved;
        }
        catch (Exception ex) { _log.LogError($"ResolveBet: {ex}"); return false; }
    }

    private static bool ResolveDecision()
    {
        if (_decResolved) return true;
        if (!ResolveShared()) return false;
        try
        {
            var bindAll = BindingFlags.Public | BindingFlags.NonPublic |
                          BindingFlags.Instance | BindingFlags.Static;

            var nextDecType = FindType("YoudaGames.MonopolyPoker.Games.Data.BlackjackNextDecisionData");
            var bjViewType = FindType("YoudaGames.MonopolyPoker.Games.View.BlackjackView");
            var decViewType = FindType("YoudaGames.MonopolyPoker.Games.View.BlackjackDecisionView");
            var handViewType = FindType("YoudaGames.MonopolyPoker.Games.View.BlackjackHandView");
            var cardDataType = FindType("YoudaGames.MonopolyPoker.Games.Data.BlackjackCardData");

            _getDecHandId = nextDecType?.GetMethod("get_handId", bindAll);
            _getDecBetAmt = nextDecType?.GetMethod("get_handValue", bindAll);
            _getBjViewEvents = bjViewType?.GetMethod("get_m_BlackjackEvents", bindAll);
            _getHandViews = bjViewType?.GetMethod("get_m_HandViews", bindAll);
            _getDecInterface = bjViewType?.GetMethod("get_m_DecisionInterface", bindAll);
            _hideDecision = decViewType?.GetMethod("HideBlackjackDecision", bindAll);
            _getDecUIGameObject = decViewType?.GetMethod("get_m_BlackjackDecisionInterface", bindAll);
            _getHVHandId = handViewType?.GetMethod("get_HandId", bindAll);
            _getHVCards = handViewType?.GetMethod("get_m_Cards", bindAll);
            _getCardValue = cardDataType?.GetMethod("get_Value", bindAll);

            _decResolved = _getDecHandId != null && _getDecBetAmt != null &&
                           _getBjViewEvents != null && _getHandViews != null &&
                           _getHVHandId != null && _getHVCards != null &&
                           _getCardValue != null;

            if (_decResolved)
                _log.LogInfo("All decision reflection targets resolved.");
            else
            {
                _log.LogWarning("Decision resolve failed:");

                if (_getDecHandId == null) _log.LogWarning("  - NextDecisionData.get_handId");
                if (_getDecBetAmt == null) _log.LogWarning("  - NextDecisionData.get_handValue");
                if (_getBjViewEvents == null) _log.LogWarning("  - BlackjackView.get_m_BlackjackEvents");
                if (_getHandViews == null) _log.LogWarning("  - BlackjackView.get_m_HandViews");
                if (_getDecInterface == null) _log.LogWarning("  - BlackjackView.get_m_DecisionInterface");
                if (_hideDecision == null) _log.LogWarning("  - HideBlackjackDecision");
                if (_getDecUIGameObject == null) _log.LogWarning("  - get_m_BlackjackDecisionInterface");
                if (_getHVHandId == null) _log.LogWarning("  - HandView.get_HandId");
                if (_getHVCards == null) _log.LogWarning("  - HandView.get_m_Cards");
                if (_getCardValue == null) _log.LogWarning("  - CardData.get_Value");
            }
            return _decResolved;
        }
        catch (Exception ex) { _log.LogError($"ResolveDecision: {ex}"); return false; }
    }

    private static bool ResolveDealer()
    {
        if (_dealerResolved) return true;
        try
        {
            var bindAll = BindingFlags.Public | BindingFlags.NonPublic |
                          BindingFlags.Instance | BindingFlags.Static;

            var roundDataType = FindType("YoudaGames.MonopolyPoker.Games.Data.BlackjackRoundData");
            var handDataType = FindType("YoudaGames.MonopolyPoker.Games.Data.BlackjackHandData");

            _getRoundDealerHand = roundDataType?.GetMethod("get_dealerHand", bindAll);
            // Primary: List<List<int>> — inner list holds possible values per card (e.g. [1,11] for Ace)
            _getHandValuesPerCard = handDataType?.GetMethod("get_handValuesPerCard", bindAll);
            // Fallback: List<int> — flat per-card point values
            _getHandValues = handDataType?.GetMethod("get_handValues", bindAll);

            _dealerResolved = _getRoundDealerHand != null &&
                              (_getHandValuesPerCard != null || _getHandValues != null);

            if (!_dealerResolved)
            {
                _log.LogWarning("Dealer resolve failed:");
                if (_getRoundDealerHand == null) _log.LogWarning("  - BlackjackRoundData.get_dealerHand");
                if (_getHandValuesPerCard == null) _log.LogWarning("  - BlackjackHandData.get_handValuesPerCard");
                if (_getHandValues == null) _log.LogWarning("  - BlackjackHandData.get_handValues");
            }
            return _dealerResolved;
        }
        catch (Exception ex) { _log.LogError($"ResolveDealer: {ex}"); return false; }
    }

    private static bool ResolveEndRound()
    {
        if (_endRoundResolved) return true;
        try
        {
            var bindAll = BindingFlags.Public | BindingFlags.NonPublic |
                          BindingFlags.Instance | BindingFlags.Static;

            var bjViewType = FindType("YoudaGames.MonopolyPoker.Games.View.BlackjackView");

            // Try various method names for getting player chips
            if (bjViewType != null)
            {
                _getPlayerChips = bjViewType.GetMethod("get_PlayerChips", bindAll) 
                               ?? bjViewType.GetMethod("get_playerChips", bindAll)
                               ?? bjViewType.GetMethod("get_Chips", bindAll)
                               ?? bjViewType.GetMethod("get_chips", bindAll);
            }

            // Don't try to resolve EndRoundData or RoundResult types - use fully dynamic discovery
            _log.LogInfo("Using fully dynamic resolution for EndRound data (types not pre-resolvable)");

            // Mark as resolved - we'll handle everything dynamically in OnEndRound
            _endRoundResolved = true;

            return _endRoundResolved;
        }
        catch (Exception ex) { _log.LogError($"ResolveEndRound: {ex}"); return false; }
    }

    // ---------------------------------------------------------------
    //  Hook : Auto-bet
    // ---------------------------------------------------------------
    private static void OnShowBetInterface(object __instance, object __0)
    {
        if (!_pluginEnabled) return; // Check if plugin is enabled

        try
        {
            if (!ResolveBet()) return;

            var betsList = _getBets.Invoke(__0, null);
            if (betsList == null) { _log.LogWarning("Auto-bet: bets list is null"); return; }

            int count = Convert.ToInt32(betsList.GetType().GetProperty("Count")?.GetValue(betsList));
            if (count == 0) { _log.LogWarning("Auto-bet: bets list empty"); return; }

            var getItem = betsList.GetType().GetMethod("get_Item");

            // Bet amount is in the "Value" property of the bet data objects; we want the minimum bet (index 0).

            long betAmount = Convert.ToInt64(getItem?.Invoke(betsList, new object[] { bet_index }));

            var events = _getDecViewEvents.Invoke(__instance, null);
            if (events == null) { _log.LogWarning("Auto-bet: events null"); return; }

            _log.LogInfo($">>> AUTO-BET: {betAmount} chips <<<");
            _blackjackAction.Invoke(events, new object[] { _getActionBet.Invoke(null, null), 0, betAmount });
            _hideBetInterface?.Invoke(__instance, null);
        }
        catch (Exception ex) { _log.LogError($"OnShowBetInterface: {ex}"); }
    }

    // ---------------------------------------------------------------
    //  Hook : Capture dealer upcard from initial deal
    // ---------------------------------------------------------------
    /// <summary>
    /// Postfix on BlackjackView.HandleOnInitRound(BlackjackRoundData).
    ///
    /// Dealer upcard reading strategy (confirmed from runtime dumps):
    ///   PRIMARY:  handValuesPerCard[upcardIdx] is List<int> of possible values.
    ///             max(list) gives 11 for Ace, face value for others.
    ///   FALLBACK: handValues[upcardIdx] is a direct int point value.
    ///
    /// upcardIdx = 1 (American deal: hole first, upcard second).
    /// If only 1 card present, upcardIdx = 0.
    /// </summary>
    private static void OnInitRound(object __instance, object __0)
    {
        if (!_pluginEnabled) return; // Check if plugin is enabled

        try
        {
            _dealerUpcardValue = -1; // reset state at start of round
            _roundStartChips = 0;
            _roundBetAmount = 0;
            _roundPlayerTotal = 0;
            _roundAction = "";

            // Try to get player chip balance
            if (_getPlayerChips != null)
            {
                try
                {
                    var chips = _getPlayerChips.Invoke(__instance, null);
                    if (chips != null)
                        _roundStartChips = Convert.ToInt64(chips);
                }
                catch { }
            }

            if (__0 == null) { _log.LogWarning("OnInitRound: BlackjackRoundData is null"); return; }
            if (!ResolveDealer()) return;

            var dealerHand = _getRoundDealerHand.Invoke(__0, null);
            if (dealerHand == null) { _log.LogWarning("OnInitRound: dealerHand null"); return; }

            bool readSuccess = false;

            // ── Primary: handValuesPerCard — List<List<int>> ──────────────────
            if (_getHandValuesPerCard != null)
            {
                var hvpcList = _getHandValuesPerCard.Invoke(dealerHand, null);
                if (hvpcList != null)
                {
                    int outerCount = Convert.ToInt32(hvpcList.GetType().GetProperty("Count")?.GetValue(hvpcList));
                    var outerGetItem = hvpcList.GetType().GetMethod("get_Item");
                    int upcardIdx = outerCount > 1 ? 1 : 0;

                    if (DEBUG)
                    {
                        _log.LogInfo($"Dealer handValuesPerCard ({outerCount} cards):");
                        for (int i = 0; i < outerCount; i++)
                        {
                            var inner = outerGetItem?.Invoke(hvpcList, new object[] { i });
                            if (inner == null) { _log.LogInfo($"  [{i}] = null"); continue; }
                            int ic = Convert.ToInt32(inner.GetType().GetProperty("Count")?.GetValue(inner));
                            var igi = inner.GetType().GetMethod("get_Item");
                            string s = "";
                            for (int k = 0; k < ic; k++) s += (k > 0 ? "," : "") + igi?.Invoke(inner, new object[] { k });
                            _log.LogInfo($"  [{i}]{(i == upcardIdx ? "upcard" : "")} = [{s}]");
                        }
                    }

                    if (upcardIdx < outerCount)
                    {
                        var innerList = outerGetItem?.Invoke(hvpcList, new object[] { upcardIdx });
                        if (innerList != null)
                        {
                            int ic = Convert.ToInt32(innerList.GetType().GetProperty("Count")?.GetValue(innerList));
                            var igi = innerList.GetType().GetMethod("get_Item");
                            int maxVal = 0;
                            for (int k = 0; k < ic; k++)
                            {
                                int v = Convert.ToInt32(igi?.Invoke(innerList, new object[] { k }));
                                if (v > maxVal) maxVal = v;
                            }
                            if (maxVal > 0) { _dealerUpcardValue = maxVal; readSuccess = true; }
                        }
                    }
                }
            }

            // ── Fallback: handValues — List<int> ─────────────────────────────
            if (!readSuccess && _getHandValues != null)
            {
                var hvList = _getHandValues.Invoke(dealerHand, null);
                if (hvList != null)
                {
                    int c = Convert.ToInt32(hvList.GetType().GetProperty("Count")?.GetValue(hvList));
                    var gi = hvList.GetType().GetMethod("get_Item");
                    int upcardIdx = c > 1 ? 1 : 0;

                    if (DEBUG)
                    {
                        _log.LogInfo($"Dealer handValues fallback ({c} entries):");
                        for (int i = 0; i < c; i++)
                            _log.LogInfo($"  [{i}]{(i == upcardIdx ? "upcard" : "")} = {gi?.Invoke(hvList, new object[] { i })}");
                    }

                    if (upcardIdx < c)
                    {
                        int v = Convert.ToInt32(gi?.Invoke(hvList, new object[] { upcardIdx }));
                        if (v > 0) { _dealerUpcardValue = v; readSuccess = true; }
                    }
                }
            }

            if (readSuccess)
                _log.LogInfo($"Dealer upcard value stored: {_dealerUpcardValue}");
            else
            {
                _log.LogError("OnInitRound: could not determine dealer upcard value. Disabling plugin.");
                _pluginEnabled = false;
            }
        }
        catch (Exception ex) { _log.LogError($"OnInitRound: {ex}"); }
    }

    // ---------------------------------------------------------------
    //  Hook : Auto-decide hit/stand using basic strategy
    // ---------------------------------------------------------------
    private static void OnHandleNextDecision(object __instance, object __0, object __1)
    {
        if (!_pluginEnabled) return; // Check if plugin is enabled

        try
        {
            if (!ResolveDecision()) return;

            if (DEBUG) DumpRoundData(__0);

            // ── Check if decision UI is showing (only active for client player) ──
            var decIntf = _getDecInterface?.Invoke(__instance, null);
            if (decIntf == null) return;

            if (_getDecUIGameObject != null)
            {
                var decUIGO = _getDecUIGameObject.Invoke(decIntf, null);
                if (decUIGO != null)
                {
                    var activeSelfProp = decUIGO.GetType().GetProperty("activeSelf");
                    if (activeSelfProp != null)
                    {
                        bool isActive = Convert.ToBoolean(activeSelfProp.GetValue(decUIGO));
                        if (!isActive) return;
                      }
                }
            }

            // ── Read decision data ──
            int handId = Convert.ToInt32(_getDecHandId.Invoke(__0, null));
            long betAmt = Convert.ToInt64(_getDecBetAmt.Invoke(__0, null));
            int dealerValue = _dealerUpcardValue;           // direct point value (2-11), -1 = unknown

            // ── Player hand cards + analysis ──
            int handTotal = CalculateHandTotal(__instance, handId, out bool isSoft, out int[] cardEnums);
            if (handTotal < 0)
            {
                _log.LogError($"CRITICAL: Could not read cards for hand {handId} - disabling plugin");
                _pluginEnabled = false; // Disable plugin entirely
                return; // Exit hook
            }

            // Store round info for end-round logging
            if (_roundBetAmount == 0) // First decision of the round
            {
                _roundBetAmount = betAmt;
                _roundPlayerTotal = handTotal;

                // Capture starting chips from decision data
                try
                {
                    var bindAll = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                    var dataType = __0.GetType();
                    var chipsMethod = dataType.GetMethod("get_playersChips", bindAll)
                                   ?? dataType.GetMethod("get_PlayerChips", bindAll);

                    if (chipsMethod != null)
                    {
                        var chips = chipsMethod.Invoke(__0, null);
                        if (chips != null)
                        {
                            long currentChips = Convert.ToInt64(chips);
                            _roundStartChips = currentChips; // Track chips AFTER bet is deducted for net profit
                            _log.LogInfo($"Captured starting chips (after bet): current={currentChips}, bet={betAmt}");
                        }
                    }
                    else
                    {
                        _log.LogWarning("Could not find get_playersChips method");
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"Failed to capture starting chips: {ex.Message}");
                }
            }

            // ── Check for pair (for splitting) ──
            bool isPair = cardEnums.Length == 2 && 
                          CardEnumToBlackjackValue(cardEnums[0]) == 
                          CardEnumToBlackjackValue(cardEnums[1]);
            int pairValue = isPair ? CardEnumToBlackjackValue(cardEnums[0]) : 0;

            // ── Basic strategy decision ──
            var events = _getBjViewEvents.Invoke(__instance, null);
            if (events == null) { _log.LogWarning("Auto-decide: events null"); return; }

            string action = dealerValue > 0
                ? BasicStrategyAction(handTotal, isSoft, dealerValue, isPair, pairValue, cardEnums.Length)
                : (handTotal <= 11 ? "HIT" : "STAND"); // fallback if dealer upcard still unknown

            object actionStr;
            string handDesc = $"player={handTotal}{(isSoft ? "(soft)" : "")}{(isPair ? $" pair={pairValue}" : "")} dealer={dealerValue}";

            switch (action)
            {
                case "SPLIT":
                    actionStr = _getActionSplit.Invoke(null, null);
                    _log.LogInfo($">>> Hand {handId}: {handDesc} -> SPLIT <<<");
                    break;
                case "DOUBLE":
                    actionStr = _getActionDouble.Invoke(null, null);
                    _log.LogInfo($">>> Hand {handId}: {handDesc} -> DOUBLE <<<");
                    break;
                case "HIT":
                    actionStr = _getActionHit.Invoke(null, null);
                    _log.LogInfo($">>> Hand {handId}: {handDesc} -> HIT <<<");
                    break;
                default: // STAND
                    actionStr = _getActionStand.Invoke(null, null);
                    _log.LogInfo($">>> Hand {handId}: {handDesc} -> STAND <<<");
                    break;
            }

            _blackjackAction.Invoke(events, new object[] { actionStr, handId, betAmt });
            _hideDecision?.Invoke(decIntf, null);

            // Store last action for end-round logging
            _roundAction = action;
        }
        catch (Exception ex) { _log.LogError($"OnHandleNextDecision: {ex}"); }
    }

    // ---------------------------------------------------------------
    //  Hook : Capture round results and log to database
    // ---------------------------------------------------------------
    /// <summary>
    /// Postfix on BlackjackView.HandleOnEndRound(BlackjackEndRoundData).
    /// Captures final chip balance, outcome, and profit to log complete round data.
    /// </summary>
    private static void OnEndRound(object __instance, object __0)
    {
        if (!_pluginEnabled) return;

        try
        {
            if (__0 == null) { _log.LogWarning("OnEndRound: parameter is null"); return; }
            ResolveEndRound(); // Initialize resolver

            // Dump the parameter type to discover correct structure
            if (DEBUG)
            {
                _log.LogInfo($"OnEndRound parameter type: {__0.GetType().FullName}");
                DumpRoundData(__0);
            }

            // Extract data from BlackjackEndRoundData
            string outcome = "UNKNOWN";
            long finalChips = _roundStartChips;
            long amountWon = 0;
            long amountBet = 0;
            bool busted = false;
            bool blackjack = false;
            bool foundData = false;

            var bindAll = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

            // Look for the "hands" collection in BlackjackEndRoundData
            try
            {
                var paramType = __0.GetType();
                var handsMethod = paramType.GetMethod("get_hands", bindAll);

                if (handsMethod != null)
                {
                    var hands = handsMethod.Invoke(__0, null);
                    if (hands != null)
                    {
                        var handsType = hands.GetType();
                        var countProp = handsType.GetProperty("Count");
                        if (countProp != null)
                        {
                            int count = Convert.ToInt32(countProp.GetValue(hands));
                            _log.LogInfo($"Found {count} hand(s) in round results");

                            if (count > 0)
                            {
                                var getItem = handsType.GetMethod("get_Item");
                                var firstHand = getItem?.Invoke(hands, new object[] { 0 });

                                if (firstHand != null)
                                {
                                    var handType = firstHand.GetType();

                                    // Extract key data from BlackjackHandData
                                    var amountWonMethod = handType.GetMethod("get_amountWon", bindAll);
                                    var amountBetMethod = handType.GetMethod("get_amountBet", bindAll);
                                    var bustedMethod = handType.GetMethod("get_busted", bindAll);
                                    var blackjackMethod = handType.GetMethod("get_blackjack", bindAll);

                                    if (amountWonMethod != null)
                                    {
                                        amountWon = Convert.ToInt64(amountWonMethod.Invoke(firstHand, null));
                                        _log.LogInfo($"Amount won: {amountWon}");
                                    }

                                    if (amountBetMethod != null)
                                    {
                                        amountBet = Convert.ToInt64(amountBetMethod.Invoke(firstHand, null));
                                        _log.LogInfo($"Amount bet: {amountBet}");
                                    }

                                    if (bustedMethod != null)
                                    {
                                        busted = Convert.ToBoolean(bustedMethod.Invoke(firstHand, null));
                                        _log.LogInfo($"Busted: {busted}");
                                    }

                                    if (blackjackMethod != null)
                                    {
                                        blackjack = Convert.ToBoolean(blackjackMethod.Invoke(firstHand, null));
                                        _log.LogInfo($"Blackjack: {blackjack}");
                                    }

                                    foundData = true;

                                    // Derive outcome from the data
                                    if (blackjack)
                                        outcome = "BLACKJACK";
                                    else if (busted)
                                        outcome = "BUST";
                                    else if (amountWon > amountBet)
                                        outcome = "WIN";
                                    else if (amountWon == amountBet)
                                        outcome = "PUSH";
                                    else
                                        outcome = "LOSE";

                                    _log.LogInfo($"Derived outcome: {outcome}");
                                }
                            }
                        }
                    }
                }
                else
                {
                    _log.LogWarning("Could not find get_hands method on BlackjackEndRoundData");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Error extracting round data: {ex.Message}");
                if (DEBUG) _log.LogError($"Stack trace: {ex.StackTrace}");
            }

            // Get current chip balance from BlackjackView
            bool foundChips = false;
            try
            {
                var viewType = __instance.GetType();
                var chipsMethod = viewType.GetMethod("get_playersChips", bindAll)
                               ?? viewType.GetMethod("get_PlayerChips", bindAll)
                               ?? viewType.GetMethod("get_playerChips", bindAll)
                               ?? viewType.GetMethod("get_Chips", bindAll)
                               ?? viewType.GetMethod("get_chips", bindAll);

                    if (chipsMethod != null)
                {
                    var chips = chipsMethod.Invoke(__instance, null);
                    if (chips != null)
                    {
                        finalChips = Convert.ToInt64(chips);
                        foundChips = true;
                        _log.LogInfo($"Got final chips from BlackjackView: {finalChips}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Could not get chip balance from view: {ex.Message}");
            }

            // Calculate profit
            long profit = 0;

            if (foundData)
            {
                // Primary: Calculate net profit from amountWon and amountBet
                // amountWon is the total returned (includes original bet if won, 0 if lost)
                profit = amountWon - amountBet;
                _log.LogInfo($"Calculated profit: {amountWon} (won) - {amountBet} (bet) = {profit}");

                // Update final chips if we couldn't read it directly
                if (!foundChips && _roundStartChips > 0)
                {
                    finalChips = _roundStartChips + profit;
                    _log.LogInfo($"Calculated final chips: {_roundStartChips} + {profit} = {finalChips}");
                }
            }
            else
            {
                // Fallback: Calculate from chip balance change
                if (foundChips && _roundStartChips > 0)
                {
                    profit = finalChips - _roundStartChips;
                    _log.LogInfo($"Calculated profit from chip change: {finalChips} - {_roundStartChips} = {profit}");
                }
                else
                {
                    _log.LogWarning($"Could not calculate profit: foundData={foundData}, foundChips={foundChips}, startChips={_roundStartChips}, bet={_roundBetAmount}");
                }
            }

            // Log to database
            _statsDatabase?.LogRound(
                betAmount: _roundBetAmount,
                chipBalance: finalChips,
                outcome: outcome,
                playerTotal: _roundPlayerTotal,
                dealerUpcard: _dealerUpcardValue,
                action: _roundAction,
                profit: profit);

            // Update session statistics
            _sessionNetProfit += profit;
            _sessionRoundsPlayed++;

            _log.LogInfo($"Round complete: bet={_roundBetAmount}, outcome={outcome}, profit={profit:+#;-#;0}, balance={finalChips}");
            _log.LogInfo($"SESSION: {_sessionRoundsPlayed} rounds, NET profit={_sessionNetProfit:+#;-#;0} ---");

            // Reset round state
            _roundBetAmount = 0;
            _roundStartChips = 0;
            _roundPlayerTotal = 0;
            _roundAction = "";
        }
        catch (Exception ex) { _log.LogError($"OnEndRound: {ex}"); }
    }

    // ---------------------------------------------------------------
    //  Basic strategy (full strategy with double/split)
    // ---------------------------------------------------------------
    /// <summary>
    /// Returns "HIT", "STAND", "DOUBLE", or "SPLIT".
    /// dealerValue: 2-11 where 11 = Ace showing.
    /// isSoft: player hand contains an Ace still counted as 11.
    /// isPair: two cards with same value (for splitting).
    /// pairValue: the value of the pair (2-11).
    /// cardCount: number of cards in hand (only double on 2 cards).
    /// </summary>
    private static string BasicStrategyAction(int playerTotal, bool isSoft, int dealerValue, 
                                               bool isPair, int pairValue, int cardCount)
    {
        bool canDouble = cardCount == 2;

        // ── Pair splitting (only on initial 2-card hand) ──
        if (isPair && cardCount == 2)
        {
            if (pairValue == 11)  // Aces
                return "SPLIT";
            if (pairValue == 10)  // 10s
                return "STAND";
            if (pairValue == 9)   // 9s: split vs 2-9 except 7
                return (dealerValue >= 2 && dealerValue <= 9 && dealerValue != 7) ? "SPLIT" : "STAND";
            if (pairValue == 8)   // 8s
                return "SPLIT";
            if (pairValue == 7)   // 7s: split vs 2-7
                return (dealerValue >= 2 && dealerValue <= 7) ? "SPLIT" : "HIT";
            if (pairValue == 6)   // 6s: split vs 2-6
                return (dealerValue >= 2 && dealerValue <= 6) ? "SPLIT" : "HIT";
            if (pairValue == 5)   // 5s: double vs 2-9
                return (canDouble && dealerValue >= 2 && dealerValue <= 9) ? "DOUBLE" : "HIT";
            if (pairValue == 4)   // 4s: split vs 5-6
                return (dealerValue == 5 || dealerValue == 6) ? "SPLIT" : "HIT";
            if (pairValue <= 3)   // 2s, 3s: split vs 2-7
                return (dealerValue >= 2 && dealerValue <= 7) ? "SPLIT" : "HIT";
        }

        // ── Soft totals (Ace counted as 11) ──
        if (isSoft)
        {
            if (playerTotal >= 20) return "STAND";  // Soft 20+
            if (playerTotal == 19)                   // Soft 19 (A,8)
                return (canDouble && dealerValue == 6) ? "DOUBLE" : "STAND";
            if (playerTotal == 18)                   // Soft 18 (A,7)
            {
                if (canDouble && dealerValue >= 2 && dealerValue <= 6) return "DOUBLE";
                if (dealerValue >= 9) return "HIT";
                return "STAND";
            }
            if (playerTotal == 17)                   // Soft 17 (A,6)
                return (canDouble && dealerValue >= 3 && dealerValue <= 6) ? "DOUBLE" : "HIT";
            if (playerTotal == 16)                   // Soft 16 (A,5)
                return (canDouble && dealerValue >= 4 && dealerValue <= 6) ? "DOUBLE" : "HIT";
            if (playerTotal == 15)                   // Soft 15 (A,4)
                return (canDouble && dealerValue >= 4 && dealerValue <= 6) ? "DOUBLE" : "HIT";
            if (playerTotal == 14)                   // Soft 14 (A,3)
                return (canDouble && dealerValue >= 5 && dealerValue <= 6) ? "DOUBLE" : "HIT";
            if (playerTotal == 13)                   // Soft 13 (A,2)
                return (canDouble && dealerValue >= 5 && dealerValue <= 6) ? "DOUBLE" : "HIT";
            return "HIT";                            // Soft 12 or less
        }

        // ── Hard totals ──
        if (playerTotal >= 17) return "STAND";
        if (playerTotal >= 13 && playerTotal <= 16)  // 13-16: stand vs 2-6
            return (dealerValue >= 2 && dealerValue <= 6) ? "STAND" : "HIT";
        if (playerTotal == 12)                       // 12: stand vs 4-6
            return (dealerValue >= 4 && dealerValue <= 6) ? "STAND" : "HIT";
        if (playerTotal == 11)                       // 11: always double
            return canDouble ? "DOUBLE" : "HIT";
        if (playerTotal == 10)                       // 10: double vs 2-9
            return (canDouble && dealerValue >= 2 && dealerValue <= 9) ? "DOUBLE" : "HIT";
        if (playerTotal == 9)                        // 9: double vs 3-6
            return (canDouble && dealerValue >= 3 && dealerValue <= 6) ? "DOUBLE" : "HIT";
        return "HIT";                                // 8 or less
    }

    // ---------------------------------------------------------------
    //  Card total calculation from hand views
    // ---------------------------------------------------------------
    private static int CardEnumToBlackjackValue(int enumValue)
    {
        if (enumValue == 12) return 11;
        if (enumValue >= 8) return 10;
        if (enumValue >= 0) return enumValue + 2;
        return 0;
    }

    private static int CardEnumToBlackjackValue(int enumValue, ref int aceCount)
    {
        if (enumValue == 12) { aceCount++; return 11; }
        if (enumValue >= 8) return 10;
        if (enumValue >= 0) return enumValue + 2;
        return 0;
    }

    private static int CalculateHandTotal(object blackjackView, int handId, out bool isSoft, out int[] cardEnums)
    {
        isSoft = false;
        cardEnums = new int[0];
        try
        {
            var handViewsList = _getHandViews.Invoke(blackjackView, null);
            if (handViewsList == null) { _log.LogWarning("m_HandViews is null"); return -1; }

            int viewCount = Convert.ToInt32(handViewsList.GetType().GetProperty("Count")?.GetValue(handViewsList));
            var getItem = handViewsList.GetType().GetMethod("get_Item");

            for (int i = 0; i < viewCount; i++)
            {
                var handView = getItem?.Invoke(handViewsList, new object[] { i });
                if (handView == null) continue;

                if (Convert.ToInt32(_getHVHandId.Invoke(handView, null)) != handId) continue;

                var cardsList = _getHVCards.Invoke(handView, null);
                if (cardsList == null) { _log.LogWarning($"m_Cards null for hand {handId}"); return -1; }

                int cardCount = Convert.ToInt32(cardsList.GetType().GetProperty("Count")?.GetValue(cardsList));
                var getCard = cardsList.GetType().GetMethod("get_Item");

                cardEnums = new int[cardCount];
                int total = 0, aceCount = 0;
                string cardDebug = "";

                for (int j = 0; j < cardCount; j++)
                {
                    var card = getCard?.Invoke(cardsList, new object[] { j });
                    if (card == null) continue;
                    int enumValue = Convert.ToInt32(_getCardValue.Invoke(card, null));
                    cardEnums[j] = enumValue;
                    cardDebug += (j > 0 ? "," : "") + enumValue;
                    total += CardEnumToBlackjackValue(enumValue, ref aceCount);
                }

                while (total > 21 && aceCount > 0) { total -= 10; aceCount--; }
                isSoft = aceCount > 0;

                _log.LogInfo($"Hand {handId}: cards=[{cardDebug}] total={total} soft={isSoft}");
                return total;
            }

            _log.LogWarning($"No hand view found with handId={handId} (searched {viewCount} views)");
            return -1;
        }
        catch (Exception ex) { _log.LogError($"CalculateHandTotal: {ex}"); return -1; }
    }

    // ---------------------------------------------------------------
    //  Diagnostic: dump any object's getters (guarded by DEBUG)
    // ---------------------------------------------------------------
    private static void DumpRoundData(object roundData)
    {
        if (!DEBUG) return;
        if (!_pluginEnabled) return; // Check if plugin is enabled

        try
        {

            _log.LogInfo("Dumping Function ----------------------------------");
            _log.LogInfo("available bets 2:  " + _getBets);
            _log.LogInfo("DealerHand:        " + _getRoundDealerHand);
            _log.LogInfo("Hand Values:       " + _getHandValues);
            _log.LogInfo("Player Chips:      " + _getPlayerChips);

            var bindAll = BindingFlags.Public | BindingFlags.NonPublic |
                          BindingFlags.Instance | BindingFlags.Static |
                          BindingFlags.DeclaredOnly;

            _log.LogInfo($"=== {roundData.GetType().Name} getters ===");
            foreach (var m in roundData.GetType().GetMethods(bindAll))
            {
                if (!m.Name.StartsWith("get_") || m.GetParameters().Length != 0) continue;
                try { _log.LogInfo($"  {m.ReturnType.Name} {m.Name} = {m.Invoke(roundData, null)}"); }
                catch { _log.LogInfo($"  {m.ReturnType.Name} {m.Name} = <invoke failed>"); }
            }
            DEBUG = false; // Only dump once per session to avoid log spam
            _log.LogInfo("Dump complete ----------------------------------");
        }
        catch (Exception ex) { _log.LogInfo($"DumpRoundData: {ex}"); }
    }

    // ---------------------------------------------------------------
    //  Plugin control methods
    // ---------------------------------------------------------------

    /// <summary>
    /// Enable or disable the plugin at runtime.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        _pluginEnabled = enabled;
        Log.LogInfo($"{PluginName} {(enabled ? "enabled" : "disabled")}");
    }

    /// <summary>
    /// Check if the plugin is currently enabled.
    /// </summary>
    public bool IsEnabled() => _pluginEnabled;

    /// <summary>
    /// Reset session statistics (rounds played and net profit).
    /// </summary>
    public void ResetSessionStats()
    {
        Log.LogInfo($"Resetting session stats. Previous: {_sessionRoundsPlayed} rounds, {_sessionNetProfit:+#;-#;0} net profit");
        _sessionNetProfit = 0;
        _sessionRoundsPlayed = 0;
        Log.LogInfo("Session stats reset.");
    }

    /// <summary>
    /// Get current session statistics.
    /// </summary>
    public (int rounds, long netProfit) GetSessionStats()
    {
        return (_sessionRoundsPlayed, _sessionNetProfit);
    }

    // ---------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------

    private static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t != null) return t;
        }
        string shortName = fullName.Contains('.')
            ? fullName[(fullName.LastIndexOf('.') + 1)..]
            : fullName;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { foreach (var t in asm.GetTypes()) if (t.Name == shortName) return t; }
            catch { }
        }
        return null;
    }

    private void PatchMethod(string typeName, string methodName, HarmonyMethod patch, bool isPostfix = false)
    {
        try
        {
            Type targetType = FindType(typeName);
            if (targetType == null) { Log.LogWarning($"  Type not found: {typeName}"); return; }

            var methods = targetType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.DeclaredOnly);

            int patched = 0;
            foreach (var method in methods)
            {
                if (method.Name != methodName) continue;
                try
                {
                    if (isPostfix) _harmony.Patch(method, postfix: patch);
                    else _harmony.Patch(method, prefix: patch);
                    patched++;
                    Log.LogInfo($"  Patched ({(isPostfix ? "postfix" : "prefix")}): {typeName}.{methodName}");
                }
                catch (Exception ex) { Log.LogWarning($"  Failed to patch {typeName}.{methodName}: {ex.Message}"); }
            }

            if (patched == 0)
                Log.LogWarning($"  No methods named '{methodName}' found on {typeName}");
        }
        catch (Exception ex) { Log.LogError($"  Error patching {typeName}.{methodName}: {ex}"); }
    }
}

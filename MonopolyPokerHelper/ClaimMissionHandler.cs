using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

using static MonopolyPokerHelper.LoggerHelper;

namespace MonopolyPokerHelper;

internal static class ClaimMissionHandler
{
    // Main polling cadence for mission scanning.
    private const float MISSION_CHECK_INTERVAL = 20f;
    // Safety limits to avoid walking very large/cyclic object graphs.
    private const int MAX_DISCOVERY_NODES = 2000;
    private const int MAX_DISCOVERY_DEPTH = 8;
    // After repeated update failures, pause before retrying.
    private const int FAILURE_BACKOFF_THRESHOLD = 3;
    private const float FAILURE_BACKOFF_SECONDS = 60f;

    private static float _timer;
    private static float _backoffRemaining;
    public static bool IsResolved { get; private set; }
    private static int _consecutiveFailures;

    private static Type _missionClaimHandlerType;
    private static Type _missionGroupType;
    private static MethodInfo _claimMissionMethod;
    private static object _missionClaimHandlerInstance;

    // Keeps track of successfully claimed missions across ticks.
    private static readonly HashSet<string> _claimedMissionKeys = [];

    // Reflection lookup cache to reduce repeated GetProperty/GetField calls.
    private static readonly Dictionary<(Type Type, string Name), MemberInfo> _memberCache = [];
    // Negative cache for members not found on a given type.
    private static readonly HashSet<(Type Type, string Name)> _missingMembers = [];

    public static void ResolveReflection()
    {
        try
        {
            if (IsResolved) return;

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;

            _missionClaimHandlerType = ReflectionHelper.FindType("YoudaGames.MonopolyPoker.MissionCollection.MissionClaimHandler")
                                     ?? ReflectionHelper.FindType("MissionClaimHandler");

            _missionGroupType = ReflectionHelper.FindType("YoudaGames.MonopolyPoker.MissionCollection.MissionGroupType")
                               ?? ReflectionHelper.FindType("MissionGroupType");

            if (_missionClaimHandlerType == null)
            {
                LogWarn("ClaimMissionHandler type not found.");
                return;
            }

            _claimMissionMethod = _missionClaimHandlerType.GetMethod(
                "ClaimMission",
                all,
                binder: null,
                types: new[] { typeof(int), typeof(int) },
                modifiers: null);

            if (_claimMissionMethod == null)
            {
                LogWarn("ClaimMission(int,int) method not found.");
                return;
            }

            _missionClaimHandlerInstance = ReflectionHelper.GetSingletonInstance(_missionClaimHandlerType);
            if (!_claimMissionMethod.IsStatic && _missionClaimHandlerInstance == null)
            {
                LogWarn("MissionClaimHandler instance not found for non-static ClaimMission.");
                return;
            }

            IsResolved = true;
            LogInfo("ClaimMissionHandler reflection resolved.");
        }
        catch (Exception ex)
        {
            LogError($"Failed to resolve ClaimMissionHandler reflection: {ex.Message}");
        }
    }

    public static void Update(float deltaTime)
    {
        if (!IsResolved) return;

        // Cooldown after consecutive failures to avoid hammering reflection/invoke paths.
        if (_backoffRemaining > 0f)
        {
            _backoffRemaining = Math.Max(0f, _backoffRemaining - deltaTime);
            if (_backoffRemaining > 0f)
                return;

            LogInfo("ClaimMissionHandler retrying after backoff.");
        }

        _timer += deltaTime;
        if (_timer < MISSION_CHECK_INTERVAL)
            return;

        _timer = 0f;

        try
        {
            var instance = _claimMissionMethod.IsStatic
                ? null
                : (_missionClaimHandlerInstance ?? ReflectionHelper.GetSingletonInstance(_missionClaimHandlerType));

            if (!_claimMissionMethod.IsStatic && instance == null)
            {
                LogError("MissionClaimHandler instance is null.");
                return;
            }

            _missionClaimHandlerInstance = instance;

            var candidates = DiscoverClaimableMissions(instance);
            // Avoid duplicate ClaimMission invokes for the same mission in the same scan.
            var uniqueCandidates = new HashSet<(int Group, int Id)>();
            foreach (var mission in candidates)
            {
                if (!uniqueCandidates.Add(mission))
                    continue;

                var key = $"{mission.Group}:{mission.Id}";
                if (_claimedMissionKeys.Contains(key))
                    continue;

                object response;
                try
                {
                    response = _claimMissionMethod.Invoke(instance, new object[] { mission.Group, mission.Id });
                }
                catch (TargetInvocationException ex)
                {
                    LogError($"ClaimMission invoke failed for mission {mission.Id} in group {mission.Group}: {ex.Message}");
                    continue;
                }
                catch (TargetException ex)
                {
                    LogError($"ClaimMission target invalid for mission {mission.Id} in group {mission.Group}: {ex.Message}");
                    continue;
                }
                catch (TargetParameterCountException ex)
                {
                    LogError($"ClaimMission signature mismatch for mission {mission.Id} in group {mission.Group}: {ex.Message}");
                    continue;
                }
                catch (MethodAccessException ex)
                {
                    LogError($"ClaimMission access denied for mission {mission.Id} in group {mission.Group}: {ex.Message}");
                    continue;
                }

                if (WasClaimSuccessful(response))
                {
                    _claimedMissionKeys.Add(key);
                    LogInfo($">>> Claimed mission {mission.Id} in group {mission.Group}");
                }
            }

            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            LogWarn($"ClaimMissionHandler update error ({_consecutiveFailures}): {ex.Message}");

            if (_consecutiveFailures >= FAILURE_BACKOFF_THRESHOLD)
            {
                _consecutiveFailures = 0;
                _backoffRemaining = FAILURE_BACKOFF_SECONDS;
                LogWarn($"ClaimMissionHandler entering backoff for {FAILURE_BACKOFF_SECONDS} seconds after repeated errors.");
            }
        }
    }

    private static List<(int Group, int Id)> DiscoverClaimableMissions(object root)
    {
        var results = new List<(int Group, int Id)>();
        if (root == null)
            return results;

        // Breadth-first walk over reachable objects from the root mission handler.
        var queue = new Queue<(object Node, int Depth)>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var processedNodes = 0;

        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (current == null || !visited.Add(current))
                continue;

            processedNodes++;
            if (processedNodes > MAX_DISCOVERY_NODES)
            {
                LogWarn($"Claim mission discovery reached node limit ({MAX_DISCOVERY_NODES}).");
                break;
            }

            if (TryExtractClaimableMission(current, out var mission))
            {
                results.Add(mission);
            }

            if (depth >= MAX_DISCOVERY_DEPTH)
                continue;

            foreach (var child in GetChildObjects(current))
            {
                if (child != null)
                    queue.Enqueue((child, depth + 1));
            }
        }

        return results;
    }

    private static bool TryExtractClaimableMission(object candidate, out (int Group, int Id) mission)
    {
        mission = default;

        if (!TryGetBool(candidate, new[] { "CanClaim", "canClaim", "Claimable", "claimable", "IsClaimable", "isClaimable" }, out var canClaim) || !canClaim)
            return false;

        if (!TryGetInt(candidate, new[] { "MissionId", "missionId", "Id", "id" }, out var missionId))
            return false;

        if (!TryGetMissionGroup(candidate, out var missionGroup))
            return false;

        mission = (missionGroup, missionId);
        return true;
    }

    private static bool TryGetMissionGroup(object source, out int group)
    {
        group = 0;

        if (TryGetInt(source, new[] { "MissionGroupType", "missionGroupType", "GroupType", "groupType" }, out group))
            return true;

        if (_missionGroupType != null)
        {
            if (TryGetEnumAsInt(source, "MissionGroupType", out group) ||
                TryGetEnumAsInt(source, "missionGroupType", out group) ||
                TryGetEnumAsInt(source, "GroupType", out group) ||
                TryGetEnumAsInt(source, "groupType", out group))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetEnumAsInt(object source, string name, out int value)
    {
        value = 0;
        var memberValue = GetMemberValue(source, name);
        if (memberValue == null)
            return false;

        var type = memberValue.GetType();
        if (!type.IsEnum)
            return false;

        value = Convert.ToInt32(memberValue);
        return true;
    }

    private static bool TryGetInt(object source, string[] names, out int value)
    {
        value = 0;

        foreach (var name in names)
        {
            var memberValue = GetMemberValue(source, name);
            if (memberValue == null)
                continue;

            try
            {
                value = Convert.ToInt32(memberValue);
                return true;
            }
            catch (InvalidCastException ex)
            {
                LogError($"Failed int conversion for member '{name}': {ex.Message}");
            }
            catch (FormatException ex)
            {
                LogError($"Failed int conversion for member '{name}': {ex.Message}");
            }
            catch (OverflowException ex)
            {
                LogError($"Failed int conversion for member '{name}': {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                LogError($"Failed int conversion for member '{name}': {ex.Message}");
            }
        }

        return false;
    }

    private static bool TryGetBool(object source, string[] names, out bool value)
    {
        value = false;

        foreach (var name in names)
        {
            var memberValue = GetMemberValue(source, name);
            if (memberValue == null)
                continue;

            try
            {
                value = Convert.ToBoolean(memberValue);
                return true;
            }
            catch (InvalidCastException ex)
            {
                LogError($"Failed bool conversion for member '{name}': {ex.Message}");
            }
            catch (FormatException ex)
            {
                LogError($"Failed bool conversion for member '{name}': {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                LogError($"Failed bool conversion for member '{name}': {ex.Message}");
            }
        }

        return false;
    }

    private static object GetMemberValue(object source, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(name));

        var type = source.GetType();
        var key = (type, name);
        MemberInfo member;

        lock (_memberCache)
        {
            // Fast path: use cached member info when available.
            if (_memberCache.TryGetValue(key, out member))
                return GetMemberValueFromMember(member, source, name);

            // Fast path: skip reflection when we've already confirmed the member is missing.
            if (_missingMembers.Contains(key))
                return null;
        }

        try
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;

            for (var current = type; current != null; current = current.BaseType)
            {
                var prop = current.GetProperty(name, all);
                if (prop != null)
                {
                    lock (_memberCache)
                    {
                        _memberCache[key] = prop;
                    }

                    return GetMemberValueFromMember(prop, source, name);
                }

                var field = current.GetField(name, all);
                if (field != null)
                {
                    lock (_memberCache)
                    {
                        _memberCache[key] = field;
                    }

                    return GetMemberValueFromMember(field, source, name);
                }
            }

            lock (_memberCache)
            {
                _missingMembers.Add(key);
            }

            return null;
        }
        catch (AmbiguousMatchException ex)
        {
            LogError($"Member lookup for '{name}' on type '{type.FullName}' is ambiguous: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            LogError($"Member lookup for '{name}' on type '{type.FullName}' is unsupported: {ex.Message}");
        }

        return null;
    }

    private static object GetMemberValueFromMember(MemberInfo member, object source, string name)
    {
        try
        {
            if (member is PropertyInfo property)
                return property.GetValue(source);

            if (member is FieldInfo field)
                return field.GetValue(source);
        }
        catch (TargetInvocationException ex)
        {
            LogError($"Reading member '{name}' failed due to invocation error: {ex.Message}");
        }
        catch (MethodAccessException ex)
        {
            LogError($"Reading member '{name}' failed due to access restriction: {ex.Message}");
        }
        catch (FieldAccessException ex)
        {
            LogError($"Reading member '{name}' failed due to field access restriction: {ex.Message}");
        }
        catch (TargetException ex)
        {
            LogError($"Reading member '{name}' failed due to invalid target: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            LogError($"Reading member '{name}' is unsupported: {ex.Message}");
        }

        return null;
    }

    private static IEnumerable<object> GetChildObjects(object source)
    {
        ArgumentNullException.ThrowIfNull(source);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance;

        var type = source.GetType();

        foreach (var property in type.GetProperties(all))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            object value;
            try
            {
                value = property.GetValue(source);
            }
            catch (TargetInvocationException ex)
            {
                LogError($"Skipping child property '{property.Name}' due to invocation error: {ex.Message}");
                continue;
            }
            catch (MethodAccessException ex)
            {
                LogError($"Skipping child property '{property.Name}' due to access restriction: {ex.Message}");
                continue;
            }
            catch (NotSupportedException ex)
            {
                LogError($"Skipping child property '{property.Name}' due to unsupported operation: {ex.Message}");
                continue;
            }

            foreach (var child in ExpandChildValue(value))
                yield return child;
        }

        foreach (var field in type.GetFields(all))
        {
            object value;
            try
            {
                value = field.GetValue(source);
            }
            catch (FieldAccessException ex)
            {
                LogError($"Skipping child field '{field.Name}' due to access restriction: {ex.Message}");
                continue;
            }
            catch (NotSupportedException ex)
            {
                LogError($"Skipping child field '{field.Name}' due to unsupported operation: {ex.Message}");
                continue;
            }
            catch (TargetException ex)
            {
                LogError($"Skipping child field '{field.Name}' due to invalid target: {ex.Message}");
                continue;
            }

            foreach (var child in ExpandChildValue(value))
                yield return child;
        }
    }

    private static IEnumerable<object> ExpandChildValue(object value)
    {
        if (value == null)
            yield break;

        if (value is string)
            yield break;

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item != null)
                    yield return item;
            }

            yield break;
        }

        if (value.GetType().IsValueType)
            yield break;

        yield return value;
    }

    private static bool WasClaimSuccessful(object response)
    {
        if (response == null)
            return false;

        if (TryGetBool(response, new[] { "Success", "success" }, out var success))
            return success;

        return false;
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public new bool Equals(object x, object y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

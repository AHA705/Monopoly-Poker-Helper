
# Snippets

## Periodic Timer

```csharp
private static float _timer;
private const float INTERVAL = 10f;

private static void UpdatePostfix()
{
    _timer += UnityEngine.Time.deltaTime;
    if (_timer < INTERVAL) return;
    _timer = 0f;
    
    // Do something every 10 seconds
}
```

## Singleton Access

```csharp
// Most game managers use the Singleton pattern
var instance = ManagerType.GetProperty("Instance").GetValue(null);
```

## Safe Reflection

```csharp
try
{
    var method = type.GetMethod("MethodName");
    if (method == null)
    {
        Log.LogWarning("Method not found!");
        return;
    }
    method.Invoke(instance, args);
}
catch (Exception ex)
{
    Log.LogError($"Reflection error: {ex.Message}");
}
```

## Patch All Overloads

```csharp
var methods = targetType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
foreach (var method in methods)
{
    if (method.Name == "TargetMethod")
    {
        _harmony.Patch(method, prefix: myPrefix);
    }
}
```

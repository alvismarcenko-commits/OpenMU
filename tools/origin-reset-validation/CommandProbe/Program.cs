using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length != 1)
{
    throw new ArgumentException("Supply the published candidate runtime directory.");
}

var runtime = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(runtime, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(runtime, "MUnique.OpenMU.GameLogic.dll"));
var expected = new Dictionary<string, (string Key, string Guid)>
{
    ["ResetChatCommandPlugIn"] = ("/reset", "90B35404-AADE-4F22-B5D2-4CD59B8BB4C8"),
    ["GrandResetChatCommandPlugIn"] = ("/grandreset", "F94D795C-F67B-48AD-8112-EA45DA39D10A"),
    ["ResetInfoChatCommandPlugIn"] = ("/resetinfo", "79F2C2C2-2E4C-4F4B-8A74-4227D1209D27"),
};
var results = new List<object>();
foreach (var pair in expected)
{
    var type = assembly.GetType("MUnique.OpenMU.GameLogic.Resets." + pair.Key, throwOnError: true)!;
    var instance = Activator.CreateInstance(type)!;
    var key = (string)type.GetProperty("Key")!.GetValue(instance)!;
    var guid = type.GetCustomAttributesData().Single(a => a.AttributeType.Name == "GuidAttribute")
        .ConstructorArguments[0].Value?.ToString();
    var registered = type.GetCustomAttributesData().Any(a => a.AttributeType.Name == "PlugInAttribute");
    if (key != pair.Value.Key || !string.Equals(guid, pair.Value.Guid, StringComparison.OrdinalIgnoreCase) || !registered)
    {
        throw new InvalidOperationException("Published command registration mismatch: " + pair.Key);
    }

    results.Add(new { type = pair.Key, key, guid, registered });
}

var feature = assembly.GetType("MUnique.OpenMU.GameLogic.Resets.ResetFeaturePlugIn", throwOnError: true)!;
if (!feature.GetInterfaces().Any(i => i.Name == "IDisabledByDefault"))
{
    throw new InvalidOperationException("Reset feature default opt-out was unexpectedly changed.");
}

Console.WriteLine(JsonSerializer.Serialize(new { commands = results, feature_disabled_by_default = true }));

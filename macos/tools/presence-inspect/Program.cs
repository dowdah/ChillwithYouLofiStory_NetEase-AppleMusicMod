using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Offline metadata/CIL inspection only. Never Assembly.Load or execute target code.
if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: presence-inspect <managed-assembly.dll>");
    return 2;
}
string path = Path.GetFullPath(args[0]);
using var assembly = AssemblyDefinition.ReadAssembly(path);
var types = Walk(assembly.MainModule.Types).ToArray();
var presenceReferences = new List<object>();
var presenceStrings = new List<object>();
var steamLifecycle = new List<object>();
var apiSignatures = new List<string>();
foreach (var type in types)
foreach (var method in type.Methods)
{
    if (type.Namespace == "Steamworks" &&
        (type.Name == "SteamFriends" || type.Name == "SteamUser" || type.Name == "SteamUtils" ||
         type.Name == "InteropHelp" || type.Name == "SteamAPI") &&
        (method.Name.Contains("RichPresence") || method.Name == "BLoggedOn" ||
         method.Name == "GetSteamID" || method.Name == "GetAppID" ||
         method.Name == "GetHSteamUser" || method.Name == "TestIfAvailableClient")) apiSignatures.Add(method.FullName);
    if (!method.HasBody) continue;
    foreach (var instruction in method.Body.Instructions)
    {
        if (instruction.Operand is MethodReference reference)
        {
            if (reference.Name.Contains("RichPresence"))
                presenceReferences.Add(new { method = method.FullName, instruction.Offset, target = reference.FullName });
            if (type.FullName == "NestopiSystem.Steam.SteamManager" &&
                reference.DeclaringType.Namespace == "Steamworks")
                steamLifecycle.Add(new { method = method.FullName, instruction.Offset, target = reference.FullName });
        }
        if (instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string value &&
            (value == "steam_display" || value.StartsWith("#Status", StringComparison.Ordinal) ||
             value.Contains("RichPresence", StringComparison.OrdinalIgnoreCase)))
            presenceStrings.Add(new { method = method.FullName, instruction.Offset, value });
    }
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    artifact = Path.GetFileName(path),
    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
    types = types.Length,
    methodsWithBody = types.Sum(t => t.Methods.Count(m => m.HasBody)),
    presenceReferences,
    presenceStrings,
    steamLifecycle,
    apiSignatures,
    identityFields = types.Where(t => t.Namespace == "Steamworks" &&
        (t.Name == "HSteamUser" || t.Name == "AppId_t" || t.Name == "CSteamID"))
        .SelectMany(t => t.Fields.Select(f => f.FullName)).ToArray(),
    limits = "Static CIL/member inspection only; absence of direct references does not establish absence of dynamically loaded code or server templates."
}, new JsonSerializerOptions { WriteIndented = true }));
return 0;

static IEnumerable<TypeDefinition> Walk(IEnumerable<TypeDefinition> input)
{
    foreach (var type in input)
    {
        yield return type;
        foreach (var nested in Walk(type.NestedTypes)) yield return nested;
    }
}

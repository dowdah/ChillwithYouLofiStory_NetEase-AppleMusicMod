using Mono.Cecil;
using System.Security.Cryptography;
// Offline metadata/CIL only; never load or invoke game code.
if (args.Length < 1 || args.Length > 2) return 2;
using var assembly = AssemblyDefinition.ReadAssembly(args[0]);
Console.WriteLine("sha256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))).ToLowerInvariant());
var types = Walk(assembly.MainModule.Types).ToArray();
if (args.Length == 1)
{
    foreach (var type in types.Where(t => t.FullName.Contains("Timer", StringComparison.OrdinalIgnoreCase) ||
                 t.FullName.Contains("Pomodoro") || t.FullName.Contains("Countup")))
        Console.WriteLine(type.FullName);
    return 0;
}
foreach (var type in types.Where(t => t.FullName == args[1] || t.DeclaringType?.FullName == args[1]))
{
    Console.WriteLine("TYPE " + type.FullName + " BASE " + type.BaseType);
    foreach (var field in type.Fields) Console.WriteLine("FIELD " + field.FullName + " STATIC=" + field.IsStatic + " CONST=" + field.Constant);
    foreach (var method in type.Methods)
    {
        Console.WriteLine("METHOD " + method.FullName + " STATIC=" + method.IsStatic);
        if (!method.HasBody) continue;
        foreach (var il in method.Body.Instructions) Console.WriteLine(il);
    }
}
return 0;
static IEnumerable<TypeDefinition> Walk(IEnumerable<TypeDefinition> roots)
{
    foreach (var type in roots) { yield return type; foreach (var child in Walk(type.NestedTypes)) yield return child; }
}

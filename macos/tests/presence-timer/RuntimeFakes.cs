using System;
using System.Collections.Generic;
using System.Reflection;

namespace HarmonyLib
{
    internal static class AccessTools { public static Type TypeByName(string name) => typeof(AccessTools).Assembly.GetType(name); }
    internal sealed class HarmonyMethod
    {
        public readonly string Name;
        public HarmonyMethod(Type type, string name) { Name = name; }
    }
    internal sealed class Harmony
    {
        public readonly List<string> Patches = new List<string>();
        public void Patch(MethodInfo original, HarmonyMethod postfix) => Patches.Add(original.Name + ":" + postfix.Name);
    }
}
namespace UnityEngine { internal static class Time { public static double realtimeSinceStartupAsDouble; } }
namespace UnityEngine.SceneManagement
{
    internal struct Scene { }
    internal static class SceneManager
    {
        public static event Action<Scene> sceneUnloaded;
        public static event Action<Scene, Scene> activeSceneChanged;
        public static void Unload() => sceneUnloaded?.Invoke(default);
        public static void Change() => activeSceneChanged?.Invoke(default, default);
    }
}

// Headless mod-lader: registreert mods zonder Godot-FileAccess en maakt Harmony.PatchAll tolerant.
// Aan via STS2_MODS_DIR (map) + STS2_MODS (kommalijst, laadvolgorde) + optioneel STS2_MOD_LOC (mappen met loc-json).
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2Headless;

internal static class HeadlessModLoader
{
    public static readonly List<Assembly> Assemblies = new();
    public static bool Enabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STS2_MODS"));
    private static void Log(string s) => Console.Error.WriteLine("[MODS] " + s);

    public static void LoadAssemblies()
    {
        if (!Enabled) return;
        var dir = Environment.GetEnvironmentVariable("STS2_MODS_DIR") ?? "mods";
        var list = (List<Mod>)typeof(ModManager).GetField("_mods", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var alc = AssemblyLoadContext.GetLoadContext(typeof(ModManager).Assembly)!;
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var n = new AssemblyName(e.Name).Name;
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == n);
        };
        foreach (var name in Environment.GetEnvironmentVariable("STS2_MODS")!.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(dir, name.Trim());
            try
            {
                ModManifest? manifest = null;
                foreach (var json in Directory.GetFiles(path, "*.json"))
                {
                    using var fs = File.OpenRead(json);
                    try { manifest = ModManifest.ReadFromStream(fs, out _); } catch { }
                    if (manifest?.id != null) break;
                }
                if (manifest?.id == null) { Log($"{name}: geen manifest"); continue; }
                var asm = alc.LoadFromAssemblyPath(Path.GetFullPath(Path.Combine(path, manifest.id + ".dll")));
                var mod = new Mod { path = path, manifest = manifest, modSource = ModSource.ModsDirectory, state = ModLoadState.Loaded };
                mod.assemblies.Add(asm);
                list.Add(mod);
                Assemblies.Add(asm);
                try { var n = asm.GetTypes().Length; Log($"{manifest.id}: geladen, {n} types"); }
                catch (ReflectionTypeLoadException ex)
                {
                    Log($"{manifest.id}: {ex.Types.Count(t => t != null)} types ok, {ex.LoaderExceptions.Length} laden niet");
                    foreach (var g in ex.LoaderExceptions.Where(x => x != null).GroupBy(x => x!.Message).Take(15)) Log($"   {g.Count()}x {g.Key}");
                }
            }
            catch (Exception ex) { Log($"{name}: laden mislukt: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    public static void InitMods()
    {
        if (!Enabled) return;
        // Harmony.PatchAll(Assembly) breekt af bij de eerste mislukte patch (UI-nodes bestaan headless niet): per klasse.
        try
        {
            var h = new Harmony("sts2headless.modloader");
            h.Patch(AccessTools.Method(typeof(Harmony), nameof(Harmony.PatchAll), new[] { typeof(Assembly) }),
                prefix: new HarmonyMethod(typeof(HeadlessModLoader), nameof(TolerantPatchAll)));
        }
        catch (Exception ex) { Log("PatchAll-patch mislukt: " + ex.Message); }
        foreach (var asm in Assemblies)
        {
            Type[] types; try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
            foreach (var t in types)
            {
                ModInitializerAttribute? attr; try { attr = t.GetCustomAttribute<ModInitializerAttribute>(); } catch { continue; }
                if (attr == null) continue;
                var m = t.GetMethod(attr.initializerMethod, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                try { m!.Invoke(null, null); Log($"{t.FullName}.{attr.initializerMethod}: ok"); }
                catch (Exception ex) { var e = ex.InnerException ?? ex; Log($"{t.FullName}.{attr.initializerMethod}: FOUT {e.GetType().Name}: {e.Message}\n{e.StackTrace?.Split('\n').Take(6).Aggregate("", (a, b) => a + b + "\n")}"); }
            }
        }
    }

    public static int PatchOk, PatchFail;
    public static bool TolerantPatchAll(Harmony __instance, Assembly assembly)
    {
        Type[] types; try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }
        int ok = 0, fail = 0; var fails = new List<string>();
        foreach (var t in types)
        {
            try { var r = __instance.CreateClassProcessor(t).Patch(); if (r != null && r.Count > 0) ok++; }
            catch (Exception ex) { fail++; var e = ex.InnerException ?? ex; fails.Add($"{t.FullName}: {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
        }
        PatchOk += ok; PatchFail += fail;
        Log($"PatchAll({assembly.GetName().Name}): {ok} klassen gepatcht, {fail} mislukt");
        foreach (var f in fails.Take(40)) Log("   x " + (f.Length > 230 ? f[..230] : f));
        return false;
    }
}

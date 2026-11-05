using System;
using System.Diagnostics;
using System.Globalization;
using HarmonyLib;
using Planetbase;

namespace PlanetbaseCoop
{
    /// <summary>
    /// Tek makinede soak testi icin kancalar. Yalniz COOP_TESTHOOKS ile derlenen
    /// test DLL'inde vardir; yayin derlemesinde cagrilar derleyici tarafindan silinir.
    ///   -coopTestSpeed 1:12000,2:60000  host: tick esiginde hiz indeksini ayarlar
    ///   -coopTestDesyncTick 4000        guest: bataryayi bozup recovery'yi zorlar
    ///   -coopTestMenuTeardown           CLI oturumunu menu oturumu gibi kapatir
    /// </summary>
    internal static class CoopTestHooks
    {
#if COOP_TESTHOOKS
        public const bool HostUsesProfile = true;
#else
        public const bool HostUsesProfile = false;
#endif

#if COOP_TESTHOOKS
        private static bool sParsed;
        private static long[] sSpeedTicks = new long[0];
        private static int[] sSpeedTargets = new int[0];
        private static long sDesyncTick = -1;
        private static bool sDesyncDone;
        private static long sLastSpeedTick;
        private static long sGridPokeEvery = -1;
        private static long sLastGridPoke;

        private static void Parse()
        {
            sParsed = true;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-coopTestDesyncTick" && i + 1 < args.Length)
                    sDesyncTick = long.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-coopTestGridPoke" && i + 1 < args.Length)
                    sGridPokeEvery = long.Parse(args[i + 1], CultureInfo.InvariantCulture);
                if (args[i] == "-coopTestMenuTeardown")
                    AccessTools.Field(typeof(SyncManager), "sMenuInitiated").SetValue(null, true);
                if (args[i] == "-coopTestSpeed" && i + 1 < args.Length)
                {
                    string[] parts = args[i + 1].Split(',');
                    sSpeedTicks = new long[parts.Length];
                    sSpeedTargets = new int[parts.Length];
                    for (int p = 0; p < parts.Length; p++)
                    {
                        string[] pair = parts[p].Split(':');
                        sSpeedTargets[p] = int.Parse(pair[0], CultureInfo.InvariantCulture);
                        sSpeedTicks[p] = long.Parse(pair[1], CultureInfo.InvariantCulture);
                    }
                }
            }
            CoopPlugin.Log.LogWarning("[Coop] TEST KANCALARI AKTIF");
        }
#endif

        [Conditional("COOP_TESTHOOKS")]
        public static void OnFrame(SyncManager sync)
        {
#if COOP_TESTHOOKS
            if (!sParsed) Parse();
            if (!sync.GameLoaded) return;
            long tick = sync.TickIndex;

            if (SyncManager.Role == "host")
            {
                int target = -1;
                for (int i = 0; i < sSpeedTicks.Length; i++)
                    if (tick >= sSpeedTicks[i]) target = sSpeedTargets[i];
                if (target > CoopTimeSync.CurrentScaleIndex && tick - sLastSpeedTick > 100)
                {
                    sLastSpeedTick = tick;
                    Singleton<TimeManager>.getInstance().increaseSpeed();
                    CoopPlugin.Log.LogWarning("[Coop] TEST hiz artirildi tick=" + tick);
                }
            }

            if (sGridPokeEvery > 0 && tick - sLastGridPoke >= sGridPokeEvery)
            {
                sLastGridPoke = tick;
                Grid largest = Grid.getLargest();
                if (largest != null) largest.requestCalculateBalance();
            }

            if (SyncManager.Role == "guest" && !sDesyncDone &&
                sDesyncTick >= 0 && tick >= sDesyncTick)
            {
                Grid grid = Grid.getLargest();
                int before = Module.getOverallPowerStorage();
                if (grid != null) grid.recharge(-3000000f);
                int after = Module.getOverallPowerStorage();
                if (before != after)
                {
                    sDesyncDone = true;
                    CoopPlugin.Log.LogWarning("[Coop] TEST bilerek desync tick=" + tick +
                        " stored " + before + " -> " + after);
                }
            }
#endif
        }
    }

#if COOP_TESTHOOKS
    /// <summary>Tick icindeki her Random cekimini cagiran zincirle dosyaya yazar.</summary>
    internal static class CoopRngTrace
    {
        public static bool InTick;
        private static System.IO.StreamWriter sWriter;
        private static long sLines;

        public static void Record(string kind, string value)
        {
            if (!InTick || SyncManager.Instance == null || !SyncManager.Instance.GameLoaded ||
                sLines > 6000000) return;
            if (sWriter == null)
                sWriter = new System.IO.StreamWriter(System.IO.Path.Combine(
                    Util.getFilesFolder(), "rng-" + SyncManager.Role + ".txt"), false);
            var line = new System.Text.StringBuilder();
            line.Append(SyncManager.Instance.TickIndex).Append(' ').Append(kind).Append(' ').Append(value);
            StackTrace stack = new StackTrace(2, false);
            int taken = 0;
            for (int i = 0; i < stack.FrameCount && taken < 6; i++)
            {
                System.Reflection.MethodBase method = stack.GetFrame(i).GetMethod();
                if (method == null || method.DeclaringType == null) continue;
                string owner = method.DeclaringType.Name;
                if (owner.StartsWith("CoopRng", StringComparison.Ordinal) ||
                    owner == "RngTrace" || owner.Contains("Patch")) continue;
                line.Append(' ').Append(owner).Append('.').Append(method.Name);
                taken++;
            }
            sWriter.WriteLine(line.ToString());
            if (++sLines % 500 == 0) sWriter.Flush();
        }
    }

    [HarmonyPatch(typeof(GameManager), "fixedUpdate")]
    internal static class CoopRngTraceTick
    {
        [HarmonyPrefix] public static void Prefix() { CoopRngTrace.InTick = true; }
        [HarmonyPostfix] public static void Postfix() { CoopRngTrace.InTick = false; }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), "Range", new[] { typeof(float), typeof(float) })]
    internal static class CoopRngTraceFloat
    {
        [HarmonyPostfix]
        public static void Postfix(float __result)
        {
            CoopRngTrace.Record("F", __result.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), "Range", new[] { typeof(int), typeof(int) })]
    internal static class CoopRngTraceInt
    {
        [HarmonyPostfix]
        public static void Postfix(int __result)
        {
            CoopRngTrace.Record("I", __result.ToString(CultureInfo.InvariantCulture));
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), "value", MethodType.Getter)]
    internal static class CoopRngTraceValue
    {
        [HarmonyPostfix]
        public static void Postfix(float __result)
        {
            CoopRngTrace.Record("V", __result.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), "insideUnitSphere", MethodType.Getter)]
    internal static class CoopRngTraceSphere
    {
        [HarmonyPostfix] public static void Postfix() { CoopRngTrace.Record("S", "-"); }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), "onUnitSphere", MethodType.Getter)]
    internal static class CoopRngTraceOnSphere
    {
        [HarmonyPostfix] public static void Postfix() { CoopRngTrace.Record("O", "-"); }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), "insideUnitCircle", MethodType.Getter)]
    internal static class CoopRngTraceCircle
    {
        [HarmonyPostfix] public static void Postfix() { CoopRngTrace.Record("C", "-"); }
    }

    [HarmonyPatch(typeof(UnityEngine.Random), "InitState")]
    internal static class CoopRngTraceInit
    {
        [HarmonyPostfix]
        public static void Postfix(int seed)
        {
            CoopRngTrace.Record("SEED", seed.ToString(CultureInfo.InvariantCulture));
        }
    }
#endif
}

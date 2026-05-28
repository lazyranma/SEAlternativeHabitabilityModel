using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Extensions;
using Data.ScriptableObject.Terraformation;
using Game.Info;
using HarmonyLib;
using Manager;

#pragma warning disable IDE0051

namespace AlternativeHabitabilityModel
{
    public static class SwingModel
    {
        public const double SECONDS_PER_DAY = 86400.0;
        public const double MIN_KELWIN = -273.15;

        public const double DAY_CEILING = 0.414;
        public const double NIGHT_FLOOR = 0.75;
        public const double TRANSPORT_REF = 100000.0;

        // Asymmetric mode state — keyed by HabitabilityParametersNew instance
        internal static readonly ConditionalWeakTable<TerraformationConfig.HabitabilityParametersNew, AsymState> _asymStates =
            new ConditionalWeakTable<TerraformationConfig.HabitabilityParametersNew, AsymState>();
        public class AsymState { public double THotK; public double TColdK; }

        public static (double THotK, double TColdK) ComputeExtremes(
            TerraformationConfig.HabitabilityParametersNew hp, ObjectInfo objectInfo)
        {
            var tc = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.TerraformationConfig.Habitability.temperature;
            double sigma = tc.stefanBoltzmanConstant;
            double solarFlux = tc.solarFlux;

            double mirrorsStrength = hp.mirrorsStrength;
            double shadesStrength  = hp.shadesStrength;
            double albedo          = hp.albedo;
            double internalFlux    = hp.internalFlux;
            double totalHC         = hp.totalHeatCapacity;
            double pressure        = hp.pressure;
            double temperatureC    = hp.temperature;
            double gravity         = hp.gravity;

            double distM     = (double)objectInfo.DistanceToSunInAU.AuToMeters();
            double rotPeriod = objectInfo.RotationPeriod;
            double starLum   = MonoBehaviourSingleton<ObjectInfoManager>.Instance.mainObjectInfoSun.StarType.luminosity;
            double mirrorR   = Plugin.MirrorRedist.Value;
            double transpPwr = Plugin.TransportPower.Value;

            // ── 1. Flux splitting ────────────────────────────────────
            double L         = solarFlux * starLum;
            double baseFlux  = (L / (4.0 * Math.PI * distM * distM)) * (1.0 - albedo);
            double fluxDay   = baseFlux * (1.0 + mirrorsStrength * (1.0 - mirrorR)) * (1.0 - shadesStrength);
            double fluxNight = baseFlux * (mirrorsStrength * mirrorR)               * (1.0 - shadesStrength);
            double absorbed  = baseFlux * (1.0 + mirrorsStrength)                   * (1.0 - shadesStrength);

            // ── 2. Hemisphere equilibria ─────────────────────────────
            double T_eq       = Math.Pow(Math.Max(absorbed  / (4.0 * sigma), internalFlux / (4.0 * sigma)), 0.25);
            double T_eq_day   = Math.Pow(Math.Max(fluxDay   / (4.0 * sigma), internalFlux / (4.0 * sigma)), 0.25);
            double T_eq_night = Math.Pow(Math.Max(fluxNight / (4.0 * sigma), internalFlux / (4.0 * sigma)), 0.25);
            double T_atm      = temperatureC - MIN_KELWIN;

            // ── 3. Radiative cooling timescale ───────────────────────
            double tauRadDays = (totalHC > 0 && T_eq > 0)
                ? totalHC / (4.0 * sigma * Math.Pow(T_eq, 3.0)) / SECONDS_PER_DAY
                : 0.0;

            // ── 4. Rotation factor ───────────────────────────────────
            double fRot = 1.0 - Math.Exp(-Math.Max(0.0, rotPeriod) / (2.0 * Math.Max(tauRadDays, 1e-30)));

            // ── 5. Illumination contrast ─────────────────────────────
            double T_hot_raw = Math.Max(T_eq_day, T_eq_night);
            double T_cold_raw = Math.Min(T_eq_day, T_eq_night);
            double p4hot = Math.Pow(T_hot_raw, 4.0);
            double p4cold = Math.Pow(T_cold_raw, 4.0);
            double contrast = (p4hot + p4cold > 0) ? (p4hot - p4cold) / (p4hot + p4cold) : 1.0;

            // ── 6. Vacuum extremes ───────────────────────────────────
            double T_dayGh   = T_eq * (1.0 + DAY_CEILING * contrast * fRot) + (T_atm - T_eq);
            double T_nightGh = T_eq * (1.0 - NIGHT_FLOOR * contrast * fRot) + (T_atm - T_eq);

            // ── 7. Atmospheric heat transport ────────────────────────
            double columnMass        = pressure > 0 ? pressure * 101325.0 / Math.Max(gravity, 1e-10) : 0.0;
            double transportCapacity = columnMass * rotPeriod;
            double fTransRaw  = transportCapacity > 0
                ? transportCapacity / (transportCapacity + TRANSPORT_REF) : 0.0;
            double transportPower = transpPwr * pressure;
            double fTransMax  = transportPower > 0 ? transportPower / (absorbed + transportPower) : 0.0;
            double fTrans     = Math.Min(fTransRaw, fTransMax);

            // ── 8. Mix toward mean ───────────────────────────────────
            double T_hot  = T_dayGh   + (T_atm - T_dayGh)   * fTrans;
            double T_cold = T_nightGh + (T_atm - T_nightGh) * fTrans;

            return (T_hot, T_cold);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Patch 1: UpdateTemperatureSwings — full model
    //   When UpdateAverageTemperature=false (default): stores hot/cold in
    //   _asymStates and sets temperatureSwings only; average temperature is
    //   left unchanged by this patch (Patches 3–5 handle Min/Max/Deposits).
    //   When UpdateAverageTemperature=true: also recalculates average temp.
    // ═══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(TerraformationConfig.HabitabilityParametersNew), "UpdateTemperatureSwings", new[] { typeof(ObjectInfo) })]
    static class Patch_UpdateTemperatureSwings
    {

        static bool Prefix(TerraformationConfig.HabitabilityParametersNew __instance, ObjectInfo objectInfo)
        {
            if (__instance == null || objectInfo == null) return true;
            try
            {
                var (THot, TCold) = SwingModel.ComputeExtremes(__instance, objectInfo);

                if (Plugin.UpdateAverageTemperature.Value)
                    __instance.temperature = (THot + TCold) / 2.0 + SwingModel.MIN_KELWIN;
                else
                {
                    SwingModel._asymStates.Remove(__instance);
                    SwingModel._asymStates.Add(__instance, new SwingModel.AsymState { THotK = THot, TColdK = TCold });
                }

                __instance.temperatureSwings = (THot - TCold) / 2.0;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"AlternativeHabitabilityModel: error in UpdateTemperatureSwings, falling back to vanilla: {ex.GetType().Name}: {ex.Message}");
                return true;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Patch 2: UpdateTotalHeatCapacity — full replacement
    //   Computes HC from configurable parameters with √P_rot scaling on rock.
    // ═══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(TerraformationConfig.HabitabilityParametersNew), "UpdateTotalHeatCapacity", new[] { typeof(ObjectInfo) })]
    static class Patch_UpdateTotalHeatCapacity_Full
    {
        private static readonly HashSet<string> _loggedBodies = new HashSet<string>();

        private static void LogRotationPeriod(ObjectInfo oi, double rotPeriod)
        {
            if (_loggedBodies.Add(oi.ObjectName))
                Plugin.Log?.LogInfo($"  [HC] {oi.ObjectName}: RotationPeriod = {rotPeriod:F3} days");
        }

        static bool Prefix(TerraformationConfig.HabitabilityParametersNew __instance, ObjectInfo objectInfo)
        {
            if (__instance == null || objectInfo == null) return true;
            try
            {
                var tc = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.TerraformationConfig;
                var hab = tc.Habitability;

                // ── Rock HC: configurable base, scaled by √P_rot ────
                double rotPeriod = objectInfo.RotationPeriod;
                double rockHC = Plugin.BaseRockHC.Value * Math.Sqrt(Math.Max(rotPeriod, 1e-6));

                // Diagnostic: log rotation period once per body
                LogRotationPeriod(objectInfo, rotPeriod);

                // ── Ocean HC: NormalHeatDepth × √P_rot, capped by water depth ──
                double currentWater = objectInfo.CurrentWaterAmount;
                double surface = objectInfo.Surface;
                double scaledHeatDepth = Plugin.NormalHeatDepth.Value * Math.Sqrt(Math.Max(rotPeriod, 1e-6));
                double waterHeatDepth = Math.Min(
                    scaledHeatDepth,
                    (Math.Pow(currentWater, hab.water.waterScaling) * 1000.0
                     + surface * hab.water.minSurfaceCoverage) / surface);
                double oceanHC = hab.temperature.waterHeatCapacityParameter
                    * waterHeatDepth * hab.water.surfaceWaterCoverage;

                // ── Atmosphere HC: same formula as vanilla ──────────
                double currentAtmMass = objectInfo.CurrentAtmosphereMass;
                double currentPressure = __instance.pressure;
                double gravity = __instance.gravity;
                double sumAtm = objectInfo.ListRowResourcesData
                    .Where(d => d.ResourceState == Game.UI.Windows.Elements.ObjectInfoElements.RowResourcesData.EResourceState.Gas && d.Value > 0.0)
                    .GroupBy(d => d.ResourcesType)
                    .Sum(g =>
                    {
                        double frac = g.Sum(d => d.Value) / currentAtmMass;
                        return g.Key.TerraformationInfo.resourceHeatCapacity * frac
                            * currentPressure * 101325.0;
                    });
                double atmHC = sumAtm / gravity * hab.temperature.atmospherePercentage;

                // ── Set total ───────────────────────────────────────
                double newTotal = rockHC + oceanHC + atmHC;
                if (!Math.Abs(newTotal - __instance.totalHeatCapacity).IsNearZero())
                {
                    __instance.prevTotalHeatCapacity = __instance.totalHeatCapacity.IsNearZero()
                        ? newTotal
                        : __instance.totalHeatCapacity;
                    __instance.totalHeatCapacity = newTotal;
                }
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"AlternativeHabitabilityModel: error in UpdateTotalHeatCapacity, falling back to vanilla: {ex.GetType().Name}: {ex.Message}");
                return true;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Patch 3a: Propagate AsymState through copy constructor new HPN(other)
    // __instance = the freshly constructed object; __0 = other (the source).
    // ═══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(TerraformationConfig.HabitabilityParametersNew), MethodType.Constructor, new[] { typeof(TerraformationConfig.HabitabilityParametersNew) })]
    static class Patch_HabitabilityParametersNew_PropagateCtor
    {

        // Constructors are void — __result cannot be used here.
        static void Postfix(TerraformationConfig.HabitabilityParametersNew __instance,
                             TerraformationConfig.HabitabilityParametersNew __0)
        {
            if (SwingModel._asymStates.TryGetValue(__0, out var state))
                SwingModel._asymStates.Add(__instance, state);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Patch 3b: Propagate AsymState through binary operators +/-/*
    // These are static methods; __instance is null, __result is the new object,
    // __0 is the left operand (the source whose AsymState we carry forward).
    // ═══════════════════════════════════════════════════════════════════════
    [HarmonyPatch]
    static class Patch_HabitabilityParametersNew_PropagateOps
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var t = AccessTools.TypeByName(
                "Data.ScriptableObject.Terraformation.TerraformationConfig+HabitabilityParametersNew");
            if (t == null)
            {
                Plugin.Log?.LogWarning("[AltSwing] PropagateOps: HabitabilityParametersNew type not found — patch skipped.");
                yield break;
            }
            yield return AccessTools.Method(t, "op_Addition",    new[] { t, t });
            yield return AccessTools.Method(t, "op_Subtraction", new[] { t, t });
            yield return AccessTools.Method(t, "op_Multiply",    new[] { t, typeof(double) });
        }

        static void Postfix(TerraformationConfig.HabitabilityParametersNew __0,
                             TerraformationConfig.HabitabilityParametersNew __result)
        {
            if (SwingModel._asymStates.TryGetValue(__0, out var state))
                SwingModel._asymStates.Add(__result, state);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Patch 4 (asymmetric): MinTemperature
    // ═══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(TerraformationConfig.HabitabilityParametersNew), nameof(TerraformationConfig.HabitabilityParametersNew.MinTemperature), MethodType.Getter)]
    static class Patch_MinTemperature
    {

        static bool Prefix(TerraformationConfig.HabitabilityParametersNew __instance, ref double __result)
        {
            if (!SwingModel._asymStates.TryGetValue(__instance, out var state))
                return true;

            __result = Math.Max(SwingModel.MIN_KELWIN,
                state.TColdK + SwingModel.MIN_KELWIN);
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Patch 5 (asymmetric): MaxTemperature
    // ═══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(TerraformationConfig.HabitabilityParametersNew), nameof(TerraformationConfig.HabitabilityParametersNew.MaxTemperature), MethodType.Getter)]
    static class Patch_MaxTemperature
    {

        static bool Prefix(TerraformationConfig.HabitabilityParametersNew __instance, ref double __result)
        {
            if (!SwingModel._asymStates.TryGetValue(__instance, out var state))
                return true;

            __result = state.THotK + SwingModel.MIN_KELWIN;
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Patch 6 (asymmetric): UpdateDepositStates — use T_hot/T_cold
    // ═══════════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(TerraformationConfig.HabitabilityParametersNew), "UpdateDepositStates", new[] { typeof(ObjectInfo) })]
    static class Patch_UpdateDepositStates_Asym
    {
        private class State { public double SavedTemp; public double SavedSwing; }

        static void Prefix(TerraformationConfig.HabitabilityParametersNew __instance, ObjectInfo objectInfo, ref State __state)
        {
            if (!SwingModel._asymStates.TryGetValue(__instance, out var asymState))
                return;

            __state = new State { SavedTemp = __instance.temperature, SavedSwing = __instance.temperatureSwings };

            // Remap so that temperature ± swing = T_hot, T_cold
            double newTemp = (asymState.THotK + asymState.TColdK) / 2.0
                             + SwingModel.MIN_KELWIN;
            double newSwing = (asymState.THotK - asymState.TColdK) / 2.0;

            __instance.temperature = newTemp;
            __instance.temperatureSwings = newSwing;
        }

        static void Postfix(TerraformationConfig.HabitabilityParametersNew __instance, State __state)
        {
            if (__state == null) return;

            __instance.temperature = __state.SavedTemp;
            __instance.temperatureSwings = __state.SavedSwing;
        }
    }
}

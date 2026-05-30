using System;
using System.Collections.Generic;
using Data;
using Data.ScriptableObject.Terraformation;
using Game.Info;
using Game.UI.Windows.Elements.ObjectInfoElements;
using HarmonyLib;
using Manager;

#pragma warning disable IDE0051

namespace AlternativeHabitabilityModel
{
    /// <summary>
    /// Stores Earth reference masses and pre-computed normalisation factors
    /// so that GasScaling=1 / WaterScaling=1 produce the same output as vanilla.
    /// Exponents are fetched from the game's TerraformConfig at init time.
    /// </summary>
    static class ScalingRefMasses
    {
        public static double Gas   { get; private set; }
        public static double Water { get; private set; }

        public static double GasExp   { get; private set; }
        public static double WaterExp { get; private set; }
        public static double GasNormFactor   { get; private set; }
        public static double WaterNormFactor { get; private set; }

        public static bool TryInit()
        {
            var oim = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (oim == null || oim.allObjectInfos == null || oim.allObjectInfos.Count == 0)
                return false;

            var earth = oim.allObjectInfos.Find(oi => oi.id == 66);
            if (earth == null) return false;

            Gas   = earth.CurrentAtmosphereMass;
            Water = earth.CurrentWaterAmount;

            var tc = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.TerraformationConfig;
            double gasScaling   = tc.Habitability.pressure.gasScaling;
            double waterScaling = tc.Habitability.water.waterScaling;

            GasExp   = 1.0 / gasScaling;
            WaterExp = waterScaling;

            GasNormFactor   = System.Math.Pow(Gas,   GasExp   - 1.0);
            WaterNormFactor = System.Math.Pow(Water, WaterExp - 1.0);
            return true;
        }
    }

    [HarmonyPatch(typeof(TerraformationConfig.HabitabilityParametersNew),
        "UpdatePressure", new[] { typeof(ObjectInfo) })]
    static class Patch_UpdatePressure_Linear
    {
        static bool Prefix(TerraformationConfig.HabitabilityParametersNew __instance,
            ObjectInfo objectInfo)
        {
            if (__instance == null || objectInfo == null) return true;

            double g    = __instance.gravity;
            double area = objectInfo.Surface;
            double mass = objectInfo.CurrentAtmosphereMass;

            __instance.pressure = mass * Plugin.GasScaling.Value * ScalingRefMasses.GasNormFactor
                                 * 1000.0 * g / area / 101325.0;

            return false;
        }
    }

    [HarmonyPatch(typeof(ObjectInfo), "get_CurrentScaledWaterAmount")]
    static class Patch_CurrentScaledWaterAmount_Linear
    {
        static bool Prefix(ObjectInfo __instance, ref double __result)
        {
            if (__instance == null) return true;
            __result = __instance.CurrentWaterAmount * Plugin.WaterScaling.Value
                       * ScalingRefMasses.WaterNormFactor;
            return false;
        }
    }

    [HarmonyPatch(typeof(ObjectInfo), "get_ScaledDownIdealWaterAmount")]
    static class Patch_ScaledDownIdealWaterAmount_Linear
    {
        static bool Prefix(ObjectInfo __instance, ref double __result)
        {
            if (__instance == null) return true;
            __result = __instance.IdealWaterAmount / (Plugin.WaterScaling.Value
                       * ScalingRefMasses.WaterNormFactor);
            return false;
        }
    }

    /// <summary>
    /// Postfix on CustomExtractFromSaveGameData — rescales deposits immediately
    /// after the template save data is loaded, before AfterLoadState warmup.
    /// </summary>
    [HarmonyPatch(typeof(ObjectInfo), "Manager.ISaveStateDataProvider.ExtractFromSaveGameData")]
    static class Patch_ExtractFromSaveGameData_Rescale
    {
        static readonly List<string> GasResourceIds = new List<string>
        {
            "id_resource_nitrogen",
            "id_resource_oxygen",
            "id_resource_co2",
            "id_resource_noblegas",
            "id_resource_hydrogen",
            "id_resource_hel3",
            "id_resource_fuel",
        };

        const string WaterResourceId = "id_resource_water";



        static void Postfix(ObjectInfo __instance)
        {
            try
            {
                var lsm = SerializedMonoBehaviourSingleton<LoadSaveManager>.Instance;
                bool isNewGame = lsm != null && lsm.ExtractingFromStartGameSave;

                // Scenario starts (2300 etc.) load a save with startGameSave=false
                // but the game hasn't finished initializing yet (StartEnd=false).
                if (!isNewGame)
                {
                    var gm = MonoBehaviourSingleton<GameManager>.Instance;
                    if (gm != null && !gm.StartEnd)
                        isNewGame = true;
                }

                if (!isNewGame && !Plugin.ScaleDepositsOnLoadingSave.Value)
                    return;
                if (!Plugin.ScaleDeposits.Value) return;
                if (__instance == null) return;

                var ot = __instance.objectTypes;
                if (ot != EObjectTypes.Planet &&
                    ot != EObjectTypes.Moons &&
                    ot != EObjectTypes.DwarfPlanet)
                    return;

                var rows = __instance.ListRowResourcesData;
                if (rows == null || rows.Count == 0) return;

                double gasTotal = 0, waterTotal = 0;

                foreach (var row in rows)
                {
                    if (row.ResourceState.HasFlag(RowResourcesData.EResourceState.Underground))
                        continue;
                    string id = row.ResourcesType.ID;
                    double v = row.Value;
                    if (v <= 0) continue;

                    if (id == WaterResourceId)
                        waterTotal += v;
                    else if (GasResourceIds.Contains(id))
                        gasTotal += v;
                }

                double gasMult = 1.0, waterMult = 1.0;

                if (gasTotal > 0)
                {
                    double gasTarget = System.Math.Pow(gasTotal,
                        ScalingRefMasses.GasExp) / ScalingRefMasses.GasNormFactor;
                    gasMult = gasTarget / gasTotal / Plugin.GasScaling.Value;
                }

                if (waterTotal > 0)
                {
                    double waterTarget = System.Math.Pow(waterTotal,
                        ScalingRefMasses.WaterExp) / ScalingRefMasses.WaterNormFactor;
                    waterMult = waterTarget / waterTotal / Plugin.WaterScaling.Value;
                }

                if (System.Math.Abs(gasMult - 1.0) < 1e-12 &&
                    System.Math.Abs(waterMult - 1.0) < 1e-12)
                    return;

                foreach (var row in rows)
                {
                    if (row.ResourceState.HasFlag(RowResourcesData.EResourceState.Underground))
                        continue;
                    string id = row.ResourcesType.ID;

                    if (id == WaterResourceId)
                        row.Value *= waterMult;
                    else if (GasResourceIds.Contains(id))
                        row.Value *= gasMult;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ExtractFromSave rescale failed: {ex}");
            }
        }
    }
}

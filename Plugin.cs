using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace AlternativeHabitabilityModel
{
    [BepInPlugin("com.althabitabilitymodel", "Alternative Habitability Model", VersionConstants.PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> AlternativeSwingModel;
        internal static ConfigEntry<bool> AlternativeMirrorModel;
        internal static ConfigEntry<bool> UpdateAverageTemperature;
        internal static ConfigEntry<double> MirrorRedist;
        internal static ConfigEntry<double> TransportPower;
        internal static ConfigEntry<double> NormalHeatDepth;
        internal static ConfigEntry<double> MirrorAreaMkm2;
        internal static ConfigEntry<double> NightFloor;
        internal static ConfigEntry<double> BaseRockHC;

        private bool _patched;

        private void Awake()
        {
            Log = Logger;

            AlternativeSwingModel = Config.Bind(
                "Temperature",
                "AlternativeSwingModel",
                true,
                "Enable the alternative temperature model.");

            AlternativeMirrorModel = Config.Bind(
                "Temperature",
                "AlternativeMirrorModel",
                true,
                "Enable the alternative mirror model.");

            MirrorAreaMkm2 = Config.Bind(
                "Temperature",
                "MirrorAreaMkm2",
                40.0,
                new ConfigDescription(
                    "Mirror area in million km². Default 40.",
                    new AcceptableValueRange<double>(0.1, 10000.0)));

            UpdateAverageTemperature = Config.Bind(
                "Temperature",
                "UpdateAverageTemperature",
                false,
                "When enabled, the average temperature is recalculated to (Min+Max)/2. When disabled, the game's original average temperature is kept and Min/Max are asymmetric.");

            MirrorRedist = Config.Bind(
                "Temperature",
                "MirrorRedist",
                0.5,
                new ConfigDescription(
                    "Fraction of mirror output redirected to the night side. Range 0.0–1.0. Default 0.5.",
                    new AcceptableValueRange<double>(0.0, 1.0)));

            TransportPower = Config.Bind(
                "Temperature",
                "TransportPower",
                250.0,
                new ConfigDescription(
                    "Maximum atmospheric heat transport at 1 atm for N₂/O₂ atmosphere (W/m²). Default 250.",
                    new AcceptableValueRange<double>(0.0, 10000.0)));

            NormalHeatDepth = Config.Bind(
                "Temperature",
                "NormalHeatDepth",
                0.5,
                new ConfigDescription(
                    "Ocean diurnal mixing depth at 1-day rotation (metres). Default 0.5.",
                    new AcceptableValueRange<double>(0.0, 1000.0)));

            NightFloor = Config.Bind(
                "Temperature",
                "NightFloor",
                0.75,
                new ConfigDescription(
                    "Fractional cold-side drop below T_eq in the vacuum limit. Default 0.75.",
                    new AcceptableValueRange<double>(0.0, 1.0)));

            BaseRockHC = Config.Bind(
                "Temperature",
                "BaseRockHC",
                50000.0,
                new ConfigDescription(
                    "Rock heat capacity at 1-day rotation (J/m²·K). Default 50000.",
                    new AcceptableValueRange<double>(1000.0, 1e8)));

            Log.LogInfo("AlternativeHabitabilityModel loaded.");
            Log.LogInfo($"  AlternativeSwingModel = {AlternativeSwingModel.Value}");
            Log.LogInfo($"  AlternativeMirrorModel = {AlternativeMirrorModel.Value}");
            Log.LogInfo($"  MirrorAreaMkm2 = {MirrorAreaMkm2.Value}");
            Log.LogInfo($"  UpdateAverageTemperature = {UpdateAverageTemperature.Value}");
            Log.LogInfo($"  MirrorRedist = {MirrorRedist.Value}");
            Log.LogInfo($"  TransportPower = {TransportPower.Value}");
            Log.LogInfo($"  NormalHeatDepth = {NormalHeatDepth.Value}");
            Log.LogInfo($"  NightFloor = {NightFloor.Value}");
            Log.LogInfo($"  BaseRockHC = {BaseRockHC.Value}");

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_patched) return;
            try { ApplyPatches(); }
            catch (Exception ex) { Log.LogDebug($"OnSceneLoaded retry: {ex.Message}"); }
        }

        private void ApplyPatches()
        {
            var harmony = new Harmony("com.alttempmodel");

            if (AlternativeSwingModel.Value)
            {
                Patch(harmony, typeof(Patch_UpdateTotalHeatCapacity_Full));
                Patch(harmony, typeof(Patch_UpdateTemperatureSwings));

                if (!UpdateAverageTemperature.Value)
                {
                    Patch(harmony, typeof(Patch_HabitabilityParametersNew_PropagateCtor));
                    Patch(harmony, typeof(Patch_HabitabilityParametersNew_PropagateOps));
                    Patch(harmony, typeof(Patch_MinTemperature));
                    Patch(harmony, typeof(Patch_MaxTemperature));
                    Patch(harmony, typeof(Patch_UpdateDepositStates_Asym));
                }
            }

            if (AlternativeMirrorModel.Value)
                Patch(harmony, typeof(Patch_GetFinalStrengthForObject));

            _patched = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void Patch(Harmony harmony, Type type)
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
                Log.LogInfo($"  Patched: {type.Name}");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to patch {type.FullName}: {ex.GetType().Name}: {ex.Message}");
                Log.LogDebug($"{ex}");
            }
        }
    }
}

using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.IO;
using System.Reflection;
using Language;
using Manager;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AlternativeHabitabilityModel
{
    [BepInPlugin("com.lazyranma.althabitabilitymodel", "Alternative Habitability Model", VersionConstants.PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> AlternativeSwingModel;
        internal static ConfigEntry<bool> AlternativeMirrorModel;
        internal static ConfigEntry<bool> AlternativeCompositionModel;
        internal static ConfigEntry<bool> AlternativePhotochemistryModel;
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
                "Swing",
                "AlternativeSwingModel",
                true,
                "Enable the alternative temperature swing model.");

            AlternativeMirrorModel = Config.Bind(
                "Mirror",
                "AlternativeMirrorModel",
                true,
                "Enable the alternative mirror model.");

            MirrorAreaMkm2 = Config.Bind(
                "Mirror",
                "MirrorAreaMkm2",
                40.0,
                new ConfigDescription(
                    "Mirror area in million km².",
                    new AcceptableValueRange<double>(0.1, 10000.0)));

            UpdateAverageTemperature = Config.Bind(
                "Swing",
                "UpdateAverageTemperature",
                false,
                "When enabled, the average temperature is recalculated to (Min+Max)/2. When disabled, the game's original average temperature is kept and Min/Max are asymmetric.");

            MirrorRedist = Config.Bind(
                "Swing",
                "MirrorRedist",
                0.5,
                new ConfigDescription(
                    "Fraction of mirror output redirected to the night side.",
                    new AcceptableValueRange<double>(0.0, 1.0)));

            TransportPower = Config.Bind(
                "Swing",
                "TransportPower",
                250.0,
                new ConfigDescription(
                    "Maximum atmospheric heat transport at 1 atm for N₂/O₂ atmosphere (W/m²).",
                    new AcceptableValueRange<double>(0.0, 10000.0)));

            NormalHeatDepth = Config.Bind(
                "Swing",
                "NormalHeatDepth",
                0.5,
                new ConfigDescription(
                    "Ocean diurnal mixing depth at 1-day rotation (metres).",
                    new AcceptableValueRange<double>(0.0, 1000.0)));

            NightFloor = Config.Bind(
                "Swing",
                "NightFloor",
                0.75,
                new ConfigDescription(
                    "Fractional cold-side drop below T_eq in the vacuum limit.",
                    new AcceptableValueRange<double>(0.0, 1.0)));

            BaseRockHC = Config.Bind(
                "Swing",
                "BaseRockHC",
                50000.0,
                new ConfigDescription(
                    "Rock heat capacity at 1-day rotation (J/m²·K).",
                    new AcceptableValueRange<double>(1000.0, 1e8)));

            // ── Composition model ──
            AlternativeCompositionModel = Config.Bind(
                "Composition",
                "AlternativeCompositionModel",
                true,
                "Enable the alternative composition model.");

            // ── Photochemistry model ──
            AlternativePhotochemistryModel = Config.Bind(
                "Photochemistry",
                "AlternativePhotochemistryModel",
                true,
                "Enable photochemical oxidation of H₂ and CH₄ in O₂-bearing atmospheres.");

            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!_patched)
                ApplyPatches();

            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void ApplyPatches()
        {
            var harmony = new Harmony("com.lazyranma.althabitabilitymodel");

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

                Log.LogInfo("Alternative Swing Model enabled.");
            }

            if (AlternativeMirrorModel.Value)
            {
                Patch(harmony, typeof(Patch_OnAfterLoadSave_Mirror));
                Patch(harmony, typeof(Patch_SaveToFile_Mirror));
                Patch(harmony, typeof(Patch_MirrorsInputFieldEndEdit));
                Patch(harmony, typeof(Patch_MirrorTargetRow_SetData));
                Patch(harmony, typeof(Patch_GetFinalStrengthForObject));
                Log.LogInfo("Alternative Mirror Model enabled.");
            }

            if (AlternativeCompositionModel.Value)
            {
                // Load custom localisation
                LoadCompositionLocalisation();

                // Replace vanilla composition curve with linear f(x)=100x
                var h = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>
                    .Instance.TerraformationConfig.Habitability;
                h.composition.curve = AnimationCurve.Linear(0f, 0f, 1f, 100f);

                Patch(harmony, typeof(Patch_UpdateComposition));
                Patch(harmony, typeof(Patch_Result_CompositionScore));
                Patch(harmony, typeof(Patch_FillInfo_Tooltip));
                Patch(harmony, typeof(Patch_CompositionState_PropagateCtor));
                Patch(harmony, typeof(Patch_CompositionState_PropagateOps));
                Log.LogInfo("Alternative Composition Model enabled.");
            }

            if (AlternativePhotochemistryModel.Value)
            {
                Patch(harmony, typeof(Patch_AfterLoadState_Photochemistry));
                Patch(harmony, typeof(Patch_UpdatePhotochemistry));
                Log.LogInfo("Alternative Photochemistry Model enabled.");
            }

            _patched = true;
        }

        private static void Patch(Harmony harmony, Type type)
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to patch {type.FullName}: {ex.GetType().Name}: {ex.Message}");
                Log.LogDebug($"{ex}");
            }
        }

        private static void LoadCompositionLocalisation()
        {
            try
            {
                string langPath = Path.Combine(
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                    "Languages");

                if (!Directory.Exists(langPath))
                {
                    Log.LogWarning($"Composition localisation folder not found: {langPath}");
                    return;
                }

                var leManager = MonoBehaviourSingleton<LEManager>.Instance;
                leManager.laungageFiles.AddCustomTranslationFromPath(langPath);
                leManager.AddCustomTranslation();

                Log.LogInfo($"Loaded composition localisation from {langPath}");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Failed to load composition localisation: {ex.Message}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Data.ScriptableObject.Terraformation;
using Game.Info;
using Game.UI.Windows.Elements.ObjectInfoElements;
using HarmonyLib;
using Language;
using Manager;
using UnityEngine;

#pragma warning disable IDE0051

namespace AlternativeHabitabilityModel
{
    /// <summary>
    /// Alternative Composition Model — two-tier breathability scoring.
    ///
    /// Tier 1 (unassisted):  breathe ambient air directly.
    /// Tier 2 (pressure suit): compress/expand ambient air to 1 atm.
    ///
    /// All curves use Unity AnimationCurve; the O₂ curve uses atmospheric
    /// ppO₂ (P × f_O₂) so it can be shown directly in the UI.
    /// </summary>

    // ── Shared constants & gas computation ────────────────────────────

    internal static class CompositionModel
    {
        internal sealed class State
        {
            public double AtmPpO2, PpCO2, EqN2;
            public double FO2, FCO2, FN2, FAr;
            public double Breathability, T1, T2;
        }

        internal static readonly
            ConditionalWeakTable<TerraformationConfig.HabitabilityParametersNew, State>
            States = new ConditionalWeakTable<TerraformationConfig.HabitabilityParametersNew, State>();

        public const double ArmstrongLimit   = 0.062;
        public const double ArNarcoticPotency = 2.3;

        internal static readonly Dictionary<string, double> MolarMasses = new()
        {
            { "id_resource_water",     18.015 },  // H₂O
            { "id_resource_nitrogen",  28.013 },  // N₂
            { "id_resource_oxygen",    31.999 },  // O₂
            { "id_resource_co2",       44.010 },  // CO₂
            { "id_resource_noblegas",  39.948 },  // Ar
            { "id_resource_hel3",       3.016 },  // ³He
            { "id_resource_volatile",  12.011 },  // C
            { "id_resource_hydrogen",   2.016 },  // H₂
            { "id_resource_fuel",      16.043 },  // CH₄
            { "id_resource_metal",     55.845 },  // Fe
            { "id_resource_raremetal", 140.116 }, // Ce
            { "id_resource_silicon",   28.086 },  // Si
            { "id_resource_uran",      238.029 }, // U
        };

        public struct GasFractions
        {
            public double f_O2, f_CO2, f_N2, f_Ar;
            public double totalMoles;
            public bool hasAtmosphere;
        }

        /// <summary>Extract mole fractions for the four tracked gases.</summary>
        public static GasFractions ComputeFractions(ObjectInfo oi)
        {
            var h = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>
                .Instance.TerraformationConfig.Habitability;
            var oxygenResType = h.composition.oxygenResourceType;

            var gf = new GasFractions();
            foreach (var row in oi.ListRowResourcesData)
            {
                if (row.ResourceState != RowResourcesData.EResourceState.Gas) continue;
                if (row.Value <= 0) continue;

                string id = row.ResourcesType.ID;
                if (!MolarMasses.TryGetValue(id, out double mw)) continue;

                double moles = row.Value / mw;
                gf.totalMoles += moles;

                if (row.ResourcesType == oxygenResType)
                    gf.f_O2 += moles;
                else if (id == "id_resource_nitrogen")
                    gf.f_N2 += moles;
                else if (id == "id_resource_co2")
                    gf.f_CO2 += moles;
                else if (id == "id_resource_noblegas")
                    gf.f_Ar += moles;
            }

            if (gf.totalMoles > 0)
            {
                gf.hasAtmosphere = true;
                gf.f_O2  /= gf.totalMoles;
                gf.f_CO2 /= gf.totalMoles;
                gf.f_N2  /= gf.totalMoles;
                gf.f_Ar  /= gf.totalMoles;
            }
            return gf;
        }

        /// <summary>
        /// Compute partial pressures at a given delivered pressure.
        /// atmPpO₂ is atmospheric (UI-displayable); alveolar is physiological.
        /// </summary>
        public static void ComputePressures(in GasFractions gf, double pDelivered,
            out double atmPpO2, out double alveolarPpO2,
            out double ppCO2, out double eqN2)
        {
            atmPpO2     = pDelivered * gf.f_O2;
            alveolarPpO2 = (pDelivered - ArmstrongLimit) * gf.f_O2 - 0.053;
            ppCO2       = pDelivered * gf.f_CO2;
            eqN2        = pDelivered * (gf.f_N2 + ArNarcoticPotency * gf.f_Ar);
        }

        /// <summary>0–1 score if you breathe at pressure p.</summary>
        public static double ComputeScore(in GasFractions gf, double p)
        {
            ComputePressures(gf, p,
                out double atmPpO2, out _, out double ppCO2, out double eqN2);

            double s = (double)Breathability.O2Curve.Evaluate((float)atmPpO2)
                     * (double)Breathability.CO2Curve.Evaluate((float)ppCO2)
                     * (double)Breathability.NarcosisCurve.Evaluate((float)eqN2);

            return Clamp01(s);
        }

        /// <summary>Collection efficiency — 1.0 at P ≥ 0.1, sharp dip to 0.</summary>
        public static double CollectionEfficiency(double pressureAtm)
        {
            if (pressureAtm >= 0.1) return 1.0;
            if (pressureAtm <= 0.0) return 0.0;
            return pressureAtm / 0.1;
        }

        internal static double Clamp01(double v)
        {
            if (v < 0.0) return 0.0;
            if (v > 1.0) return 1.0;
            return v;
        }
    }

    // ── AnimationCurve-based scoring curves ───────────────────────────

    internal static class Breathability
    {
        internal static readonly AnimationCurve O2Curve;
        internal static readonly AnimationCurve CO2Curve;
        internal static readonly AnimationCurve NarcosisCurve;

        static Breathability()
        {
            O2Curve = new AnimationCurve();
            O2Curve.AddKey(0.00f, 0.00f);   // vacuum
            O2Curve.AddKey(0.06f, 0.00f);   // death zone (alveolar ppO₂ ≈ 0)
            O2Curve.AddKey(0.10f, 0.50f);   // extreme acclimatisation (~5500 m)
            O2Curve.AddKey(0.12f, 0.80f);   // La Paz — marginal but millions live here
            O2Curve.AddKey(0.16f, 0.90f);   // Denver — adequate for most
            O2Curve.AddKey(0.21f, 1.00f);   // sea level — optimal
            O2Curve.AddKey(0.50f, 1.00f);   // optimal plateau (mild enrichment)
            O2Curve.AddKey(0.80f, 0.30f);   // pulmonary concern
            O2Curve.AddKey(1.60f, 0.05f);   // CNS risk
            O2Curve.AddKey(2.00f, 0.00f);   // lethal hyperoxia
            O2Curve.preWrapMode = WrapMode.ClampForever;
            O2Curve.postWrapMode = WrapMode.ClampForever;

            CO2Curve = new AnimationCurve();
            CO2Curve.AddKey(0.000f, 1.00f);
            CO2Curve.AddKey(0.001f, 1.00f);  // safe
            CO2Curve.AddKey(0.005f, 0.90f);  // stuffy
            CO2Curve.AddKey(0.010f, 0.70f);  // OSHA exceeded
            CO2Curve.AddKey(0.030f, 0.40f);  // distress
            CO2Curve.AddKey(0.050f, 0.10f);  // severe
            CO2Curve.AddKey(0.060f, 0.00f);  // lethal
            CO2Curve.preWrapMode = WrapMode.ClampForever;
            CO2Curve.postWrapMode = WrapMode.ClampForever;

            NarcosisCurve = new AnimationCurve();
            NarcosisCurve.AddKey(0.0f, 1.00f);
            NarcosisCurve.AddKey(2.0f, 1.00f);   // none
            NarcosisCurve.AddKey(3.0f, 0.85f);   // threshold
            NarcosisCurve.AddKey(5.0f, 0.50f);   // noticeable
            NarcosisCurve.AddKey(8.0f, 0.15f);   // severe
            NarcosisCurve.AddKey(10.0f, 0.00f);  // incapacitating
            NarcosisCurve.AddKey(11.0f, 0.00f);
            NarcosisCurve.preWrapMode = WrapMode.ClampForever;
            NarcosisCurve.postWrapMode = WrapMode.ClampForever;
        }
    }

    // ── Patch: UpdateComposition ──────────────────────────────────────

    [HarmonyPatch(typeof(TerraformationConfig.HabitabilityParametersNew), "UpdateComposition")]
    class Patch_UpdateComposition
    {
        static bool Prefix(TerraformationConfig.HabitabilityParametersNew __instance, ObjectInfo objectInfo)
        {
            var gf = CompositionModel.ComputeFractions(objectInfo);
            if (!gf.hasAtmosphere)
            {
                __instance.composition = 0.0;
                return false;
            }

            double p = __instance.pressure;

            // Tier 1 — unassisted
            double t1 = CompositionModel.ComputeScore(gf, p);

            // Tier 2 — suit delivers 1 atm of same mix, score halved
            double t2Raw = CompositionModel.ComputeScore(gf, 1.0);
            double t2 = t2Raw * 0.5 * CompositionModel.CollectionEfficiency(p);

            __instance.composition = Math.Max(t1, t2);

            // Store computed values for tooltip (follows AltSwing pattern)
            CompositionModel.States.Remove(__instance);
            if (gf.hasAtmosphere)
            {
                CompositionModel.ComputePressures(gf, p,
                    out double atmPpO2, out _, out double ppCO2, out double eqN2);
                CompositionModel.States.Add(__instance, new CompositionModel.State
                {
                    AtmPpO2 = atmPpO2, PpCO2 = ppCO2, EqN2 = eqN2,
                    FO2 = gf.f_O2, FCO2 = gf.f_CO2, FN2 = gf.f_N2, FAr = gf.f_Ar,
                    Breathability = __instance.composition, T1 = t1, T2 = t2
                });
            }

            Plugin.Log.LogDebug(
                $"AltComposition: {objectInfo.ObjectName} " +
                $"P={p:F3}atm atmPpO2={p * gf.f_O2:F3} " +
                $"T1={t1:F3} T2={t2:F3} → comp={__instance.composition:F3}");

            return false;
        }
    }

    // ── Patch: FillInfo tooltip ───────────────────────────────────────

    [HarmonyPatch(typeof(UIBasicInfoHabitabilityParameters), "FillInfo")]
    class Patch_FillInfo_Tooltip
    {
        static void Postfix(UIBasicInfoHabitabilityParameters __instance, ObjectInfo oi)
        {
            if (!Plugin.AlternativeCompositionModel.Value) return;

            var hp = oi.HabitabilityParameters;
            double score = hp.composition;

            if (!CompositionModel.States.TryGetValue(hp, out var state))
            {
                SetTooltip(__instance,
                    $"<size=14><font=\"Oxanium-Medium SDF\"><b><color=white>{L("AltComp.Header")}</color></b></font></size>\n\n{L("AltComp.NoAtmosphere")}");
                return;
            }

            double atmPpO2 = state.AtmPpO2;
            double ppCO2   = state.PpCO2;
            double eqN2    = state.EqN2;
            double t1      = state.T1;
            double t2      = state.T2;

            double co2Sub = (double)Breathability.CO2Curve.Evaluate((float)ppCO2);

            string equipHint;
            if (t2 > t1)
            {
                equipHint = score >= 0.25 ? L("AltComp.Equip.SuitBreathable")
                          : score >= 0.01 ? L("AltComp.Equip.SuitAirProc")
                          :                 L("AltComp.Equip.SuitSupplied");
            }
            else if (score >= 0.80)
            {
                equipHint = L("AltComp.Equip.Breathable");
            }
            else if (score >= 0.50)
            {
                equipHint = co2Sub < 0.95 ? L("AltComp.Equip.AirProcRec")
                          :                 L("AltComp.Equip.O2MaskRec");
            }
            else if (score >= 0.20)
            {
                equipHint = co2Sub < 0.95 ? L("AltComp.Equip.AirProcReq")
                          :                 L("AltComp.Equip.O2MaskReq");
            }
            else if (score > 0.00)
            {
                equipHint = L("AltComp.Equip.AirProcReq");
            }
            else
            {
                equipHint = L("AltComp.Equip.ClosedCircuit");
            }

            string ppO2Rating = atmPpO2 switch
            {
                < 0.10 => L("AltComp.Rating.O2.Dangerous"),  < 0.12 => L("AltComp.Rating.O2.Poor"),
                < 0.16 => L("AltComp.Rating.O2.Marginal"),   < 0.21 => L("AltComp.Rating.O2.Good"),
                < 0.50 => L("AltComp.Rating.O2.Optimal"),    < 0.80 => L("AltComp.Rating.O2.Elevated"),
                < 1.60 => L("AltComp.Rating.O2.Toxic"),      _ => L("AltComp.Rating.O2.Lethal")
            };
            string co2Rating = ppCO2 switch
            {
                < 0.001 => L("AltComp.Rating.CO2.Safe"),      < 0.005 => L("AltComp.Rating.CO2.Elevated"),
                < 0.010 => L("AltComp.Rating.CO2.Poor"),      < 0.030 => L("AltComp.Rating.CO2.Hazardous"),
                < 0.050 => L("AltComp.Rating.CO2.Dangerous"), _ => L("AltComp.Rating.CO2.Lethal")
            };
            string narcRating = eqN2 switch
            {
                < 2.0 => L("AltComp.Rating.Narc.Safe"),  < 3.0 => L("AltComp.Rating.Narc.Mild"),
                < 5.0 => L("AltComp.Rating.Narc.Moderate"),  < 8.0 => L("AltComp.Rating.Narc.Severe"),
                _ => L("AltComp.Rating.Narc.Incapacitating")
            };

            string ppO2Color = atmPpO2 >= 0.16 && atmPpO2 <= 0.50 ? "#80FF80"
                             : atmPpO2 >= 0.12 && atmPpO2 < 0.16 ? "#FFFF80"
                             : "#FF8080";
            string co2Color = ppCO2 < 0.001 ? "#80FF80" : ppCO2 < 0.005 ? "#FFFF80" : "#FF8080";
            string narcColor = eqN2 < 2.0 ? "#80FF80" : eqN2 < 3.0 ? "#FFFF80" : "#FF8080";

            string tooltip =
                $"<size=14><font=\"Oxanium-Medium SDF\"><b><color=white>{L("AltComp.Header")}</color></b></font></size>\n\n" +
                $"<color=#C0C0C0>{L("AltComp.Label.O2"),-8}</color>" +
                fmtLine(atmPpO2, "F3", L("AltComp.Unit.Atm"), state.FO2, ppO2Rating, ppO2Color) + "\n" +
                $"<color=#808080>         {string.Format(L("AltComp.Ideal.Range"), "0.19", "0.50")}</color>\n\n" +

                $"<color=#C0C0C0>{L("AltComp.Label.CO2"),-8}</color>" +
                fmtLine(ppCO2, "F3", L("AltComp.Unit.Atm"), state.FCO2, co2Rating, co2Color) + "\n" +
                $"<color=#808080>         {string.Format(L("AltComp.Ideal.Less"), "0.001")}</color>\n\n" +

                $"<color=#C0C0C0>{L("AltComp.Label.N2Ar"),-8}</color>" +
                fmtLine(eqN2, "F1", L("AltComp.Unit.AtmEq"), state.FN2 + state.FAr, narcRating, narcColor) + "\n" +
                $"<color=#808080>         {string.Format(L("AltComp.Ideal.EqLess"), "2.0")}</color>\n\n" +

                $"{string.Format(L("AltComp.Score"), $"{score * 100.0:F0}")}  " +
                $"<color=#C0C0C0>{string.Format(L("AltComp.ScoreHint"), equipHint)}</color>";

            SetTooltip(__instance, tooltip);
        }

        static void SetTooltip(UIBasicInfoHabitabilityParameters instance, string text)
        {
            var param = Traverse.Create(instance)
                .Field("composition")
                .GetValue<UIBasicInfoHabitabilitySingleParameter>();
            if (param == null) return;

            var tt = Traverse.Create(param)
                .Field("tooltip")
                .GetValue<ShowToolTip>();
            if (tt != null)
                tt.CustomTextFromCode = text;
        }

        static string L(string key) => LEManager.Get(key);

        static string fmtLine(double ppVal, string ppFmt, string unit,
            double molFrac, string rating, string color)
        {
            var ci = LEManager.GetCultureInfo();
            return string.Format(L("AltComp.Format.PpLine"),
                ppVal.ToString(ppFmt, ci),
                unit,
                (molFrac * 100.0).ToString("F1", ci),
                L("AltComp.Unit.Pct"),
                $"<color={color}>{rating}</color>");
        }
    }

    // ── State propagation (copy ctor & operators) ────────────────────

    [HarmonyPatch(typeof(TerraformationConfig.HabitabilityParametersNew),
        MethodType.Constructor, new[] { typeof(TerraformationConfig.HabitabilityParametersNew) })]
    static class Patch_CompositionState_PropagateCtor
    {
        static void Postfix(TerraformationConfig.HabitabilityParametersNew __instance,
                             TerraformationConfig.HabitabilityParametersNew __0)
        {
            if (CompositionModel.States.TryGetValue(__0, out var state))
                CompositionModel.States.Add(__instance, state);
        }
    }

    [HarmonyPatch]
    static class Patch_CompositionState_PropagateOps
    {
        static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var t = AccessTools.TypeByName(
                "Data.ScriptableObject.Terraformation.TerraformationConfig+HabitabilityParametersNew");
            if (t == null) yield break;
            yield return AccessTools.Method(t, "op_Addition",    new[] { t, t });
            yield return AccessTools.Method(t, "op_Subtraction", new[] { t, t });
            yield return AccessTools.Method(t, "op_Multiply",    new[] { t, typeof(double) });
        }

        static void Postfix(TerraformationConfig.HabitabilityParametersNew __0,
                             TerraformationConfig.HabitabilityParametersNew __result)
        {
            if (CompositionModel.States.TryGetValue(__0, out var state))
                CompositionModel.States.Add(__result, state);
        }
    }
}

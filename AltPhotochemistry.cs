using System.Collections.Generic;
using ScriptableObjectScripts;
using Data.ScriptableObject;
using Data.ScriptableObject.Terraformation;
using Game.Info;
using Game.UI.Windows.Elements.ObjectInfoElements;
using HarmonyLib;
using Manager;

#pragma warning disable IDE0051

namespace AlternativeHabitabilityModel
{
    /// <summary>
    /// Photochemical oxidation + probabilistic combustion of H₂ and CH₄.
    ///
    /// Combustion uses a flammability-limited sigmoid model:
    ///   - Fuel must be within [LFL, UFL] mole fraction and O₂ ≥ 10%
    ///   - Ignition probability P(τ) is a sigmoid of τ = Tmax / T_auto
    ///   - Below autoignition: smooth burn fraction (spark ignition)
    ///   - At/above autoignition: complete combustion (τ ≥ 1 → 100%)
    ///   - Combustion and photochemistry coexist in the same tick
    /// </summary>
    [HarmonyPatch(typeof(TerraformationConfig.HabitabilityParametersNew), "Update")]
    class Patch_UpdatePhotochemistry
    {
        // ── Flammability limits (fuel:O₂ mole ratio) ──
        // These encode the lean and rich limits correctly regardless of
        // O₂ concentration.  e.g. CH₄ at 30% in 40% O₂ → ratio 0.75 → flammable,
        // but CH₄ at 30% in 10% O₂ → ratio 3.0 → too rich to burn.
        const double H2_LEAN_RATIO = 0.05;    // mol H₂ / mol O₂ — below = too lean
        const double H2_RICH_RATIO = 17.0;    // mol H₂ / mol O₂ — above = too rich
        const double CH4_LEAN_RATIO = 0.25;   // mol CH₄ / mol O₂
        const double CH4_RICH_RATIO = 0.84;   // mol CH₄ / mol O₂
        const double MIN_O2_FRAC = 0.05;      // Minimum O₂ mole fraction (5%)

        // ── Ignition sigmoid ──
        const double COMB_SIGMOID_MID = 0.85;   // T/T_auto at P=0.5
        const double COMB_SIGMOID_STEEP = 20.0;  // Sharpness

        // ── Flame extinction ──
        // Below autoignition, a spark-ignited flame must self-propagate
        // through cold diluent.  If the flame temperature drops below
        // T_EXTINCTION, the radical chain quenches and the flame dies.
        const double DELTA_T_ADIABATIC_H2 = 2700.0;   // K, pure H₂-O₂ flame temp rise
        const double DELTA_T_ADIABATIC_CH4 = 2200.0;  // K, pure CH₄-O₂
        const double T_EXTINCTION = 900.0;             // K, below this flame self-extinguishes
        const double T_REF = 300.0;                   // K, lab reference for flammability limits

        // Molar masses — read from the shared dictionary in AltComposition.cs.
        static readonly double H2_MOLAR_MASS  = CompositionModel.MolarMasses["id_resource_hydrogen"];
        static readonly double CH4_MOLAR_MASS = CompositionModel.MolarMasses["id_resource_fuel"];
        static readonly double O2_MOLAR_MASS  = CompositionModel.MolarMasses["id_resource_oxygen"];
        static readonly double CO2_MOLAR_MASS = CompositionModel.MolarMasses["id_resource_co2"];
        static readonly double H2O_MOLAR_MASS = CompositionModel.MolarMasses["id_resource_water"];

        // ── Photochemistry constants ──
        const double H2_LIFETIME_EARTH = 2.0;
        const double CH4_LIFETIME_EARTH = 9.0;
        const double WET_DRY_RATIO = 40.0;
        const double H2O_MOLE_FRAC_EARTH = 0.016;
        const double PO2_EARTH = 0.21;
        const double TICK_YEARS = 1.0 / 12.0;

        // ── Combustion stoichiometry (mass ratios) ──
        const double H2_O2_RATIO = 8.0;       // kg O₂ per kg H₂
        const double H2_H2O_RATIO = 9.0;      // kg H₂O per kg H₂
        const double CH4_O2_RATIO = 4.0;      // kg O₂ per kg CH₄
        const double CH4_CO2_RATIO = 2.75;    // kg CO₂ per kg CH₄
        const double CH4_H2O_RATIO = 2.25;    // kg H₂O per kg CH₄

        // Autoignition temperatures (K).
        const double H2_AUTOIGNITION_K = 773.0;
        const double CH4_AUTOIGNITION_K = 853.0;
        const double KELVIN = 273.15;

        // Cached ResourceDefinitions for combustion products.
        static ResourceDefinition _waterDef;
        static ResourceDefinition _co2Def;

        // True while AfterLoadState is running — covers the pre-warm loop
        // and the final UpdateHabitabilityAndOrVisualization call.
        internal static bool IsPreWarming;

        static ResourceDefinition GetWaterDef()
        {
            if (_waterDef == null)
                _waterDef = FindResourceDef("id_resource_water");
            return _waterDef;
        }

        static ResourceDefinition GetCO2Def()
        {
            if (_co2Def == null)
                _co2Def = FindResourceDef("id_resource_co2");
            return _co2Def;
        }

        static ResourceDefinition FindResourceDef(string id)
        {
            foreach (ResourceDefinition rd in SerializedMonoBehaviourSingleton<AllScriptableObjectManager>
                .Instance.AllResourceDefinitions.ListNotEmpty)
                if (rd.ID == id) return rd;
            return null;
        }

        /// <summary>
        /// Create a new gas-phase resource row if one doesn't exist,
        /// and add it to the body's resource list.
        /// </summary>
        static RowResourcesData EnsureGasRow(ObjectInfo objectInfo, ResourceDefinition def)
        {
            // Check if row already exists.
            foreach (var r in objectInfo.ListRowResourcesData)
                if (r.ResourceState == RowResourcesData.EResourceState.Gas
                    && r.ResourcesType == def)
                    return r;

            var row = new RowResourcesData
            {
                ResourceState = RowResourcesData.EResourceState.Gas,
                ResourcesType = def,
                Value = 0,
                MiningFactor = 0f,
                ForcePrimary = false
            };

            // Match the game's own logic in UpdateDepositStates.
            var playerData = objectInfo.GetObjectInfoData(
                MonoBehaviourSingleton<GameManager>.Instance.Player);
            bool fullyExplored = true;
            if (playerData != null)
                foreach (var er in playerData.listExploredResourcesRows)
                    if (er.Value < 1.0) { fullyExplored = false; break; }

            objectInfo.AddDeposit(row, fullyExplored);
            return row;
        }

        static void Postfix(TerraformationConfig.HabitabilityParametersNew __instance,
            ObjectInfo objectInfo, bool __result)
        {
            if (!__result) return;
            if (!Plugin.AlternativePhotochemistryModel.Value) return;

            // ── Skip during pre-warm (save load) ──
            if (IsPreWarming) return;

            // ── Sweep gas resources ──
            RowResourcesData rowH2 = null, rowCH4 = null, rowO2 = null;
            RowResourcesData rowH2O = null, rowCO2 = null;
            double totalAtmMass = 0;
            double totalMoles = 0;

            foreach (var row in objectInfo.ListRowResourcesData)
            {
                if (row.ResourceState != RowResourcesData.EResourceState.Gas) continue;
                if (row.Value <= 0) continue;

                totalAtmMass += row.Value;
                if (CompositionModel.MolarMasses.TryGetValue(row.ResourcesType.ID, out double mw))
                    totalMoles += row.Value / mw;

                switch (row.ResourcesType.ID)
                {
                    case "id_resource_oxygen":   rowO2 = row; break;
                    case "id_resource_hydrogen": rowH2 = row; break;
                    case "id_resource_fuel":     rowCH4 = row; break;
                    case "id_resource_water":    rowH2O = row; break;
                    case "id_resource_co2":      rowCO2 = row; break;
                }
            }

            if (totalAtmMass <= 0) return;

            // Bail early if nothing to oxidize or no oxidant.
            bool hasO2 = rowO2 != null && rowO2.Value > 0;
            bool hasFuel = (rowH2 != null && rowH2.Value > 0)
                        || (rowCH4 != null && rowCH4.Value > 0);
            if (!hasO2 || !hasFuel) return;

            // ── Combustion model ──
            // Flammability: fuel:O₂ ratio must be within [lean, rich] limits.
            double maxTempK = __instance.MaxTemperature + KELVIN;

            // Compute mole amounts for fuel:O₂ ratio checks.
            double o2Moles = rowO2.Value / O2_MOLAR_MASS;
            double h2Moles = rowH2 != null ? rowH2.Value / H2_MOLAR_MASS : 0;
            double ch4Moles = rowCH4 != null ? rowCH4.Value / CH4_MOLAR_MASS : 0;

            double o2MoleFrac = totalMoles > 0 ? o2Moles / totalMoles : 0;
            double h2O2Ratio = o2Moles > 0 ? h2Moles / o2Moles : 0;
            double ch4O2Ratio = o2Moles > 0 ? ch4Moles / o2Moles : 0;

            // Compute τ first — flammability limits widen with temperature
            // and vanish entirely at autoignition.
            double tAuto = (rowH2 != null && rowH2.Value > 0) ? H2_AUTOIGNITION_K : CH4_AUTOIGNITION_K;
            double tau = maxTempK / tAuto;

            bool o2Sufficient, h2Flammable, ch4Flammable;

            if (tau >= 1.0)
            {
                // Autoignition: ratios and minimum O₂ are irrelevant.
                o2Sufficient = rowO2.Value > 0;
                h2Flammable = rowH2 != null && rowH2.Value > 0;
                ch4Flammable = rowCH4 != null && rowCH4.Value > 0;
            }
            else
            {
                // Widen flammability limits from room-temperature baseline.
                // expand = 1.0 at T=T_REF, →∞ at T=T_auto.
                double tauRef = T_REF / tAuto;
                double expand = (1.0 - tauRef) / (1.0 - tau);
                double effH2Lean = H2_LEAN_RATIO / expand;
                double effH2Rich = H2_RICH_RATIO * expand;
                double effCH4Lean = CH4_LEAN_RATIO / expand;
                double effCH4Rich = CH4_RICH_RATIO * expand;

                o2Sufficient = o2MoleFrac >= MIN_O2_FRAC;
                h2Flammable = o2Sufficient && rowH2 != null && rowH2.Value > 0
                            && h2O2Ratio >= effH2Lean && h2O2Ratio <= effH2Rich;
                ch4Flammable = o2Sufficient && rowCH4 != null && rowCH4.Value > 0
                            && ch4O2Ratio >= effCH4Lean && ch4O2Ratio <= effCH4Rich;
            }

            bool anyFlammable = h2Flammable || ch4Flammable;

            // Reactive mole fraction (fuel + O₂) for flame extinction model.
            double fuelMolesForExtinct = 0;
            if (h2Flammable) fuelMolesForExtinct += h2Moles;
            if (ch4Flammable) fuelMolesForExtinct += ch4Moles;
            double fReactive = totalMoles > 0 ? (fuelMolesForExtinct + o2Moles) / totalMoles : 0;

            if (anyFlammable)
            {
                double burnFraction;

                if (tau >= 1.0)
                {
                    // Complete combustion at or above autoignition.
                    burnFraction = 1.0;
                }
                else
                {
                    // Sigmoid: P(τ) = 1 / (1 + exp(−k(τ − τ₀)))
                    // Deterministic expected burn fraction per tick.
                    burnFraction = 1.0 / (1.0 + System.Math.Exp(
                        -COMB_SIGMOID_STEEP * (tau - COMB_SIGMOID_MID)));

                    // Dilution penalty: flame must self-propagate through cold
                    // buffer gas.  Margin = how far above extinction the flame
                    // temperature sits, as a fraction of the pure-mixture margin.
                    double deltaTAdiabatic = h2Flammable ? DELTA_T_ADIABATIC_H2 : DELTA_T_ADIABATIC_CH4;
                    double tFlame = maxTempK + fReactive * deltaTAdiabatic;
                    double tPureRef = maxTempK + deltaTAdiabatic;
                    double dilutionFactor;
                    if (tFlame <= T_EXTINCTION)
                        dilutionFactor = 0;
                    else
                        dilutionFactor = (tFlame - T_EXTINCTION) / (tPureRef - T_EXTINCTION);
                    burnFraction *= dilutionFactor;
                }

                double o2Avail = rowO2.Value;
                double h2Avail = rowH2?.Value ?? 0;
                double ch4Avail = rowCH4?.Value ?? 0;

                // Only burn fuels that are within flammability limits.
                double burnableH2 = h2Flammable ? h2Avail : 0;
                double burnableCH4 = ch4Flammable ? ch4Avail : 0;

                double targetH2 = burnableH2 * burnFraction;
                double targetCH4 = burnableCH4 * burnFraction;
                double o2Demand = targetH2 * H2_O2_RATIO + targetCH4 * CH4_O2_RATIO;

                double burnedH2, burnedCH4, burnedO2;
                if (o2Avail >= o2Demand)
                {
                    burnedH2 = targetH2;
                    burnedCH4 = targetCH4;
                    burnedO2 = o2Demand;
                }
                else
                {
                    double frac = o2Avail / o2Demand;
                    burnedH2 = targetH2 * frac;
                    burnedCH4 = targetCH4 * frac;
                    burnedO2 = o2Avail;
                }

                double producedH2O = burnedH2 * H2_H2O_RATIO + burnedCH4 * CH4_H2O_RATIO;
                double producedCO2 = burnedCH4 * CH4_CO2_RATIO;

                // Remove reactants.
                if (rowH2 != null) rowH2.Value = System.Math.Max(0, rowH2.Value - burnedH2);
                if (rowCH4 != null) rowCH4.Value = System.Math.Max(0, rowCH4.Value - burnedCH4);
                rowO2.Value = System.Math.Max(0, rowO2.Value - burnedO2);

                // Add products.
                if (producedH2O > 0)
                {
                    rowH2O ??= EnsureGasRow(objectInfo, GetWaterDef());
                    rowH2O.Value += producedH2O;
                }
                if (producedCO2 > 0)
                {
                    rowCO2 ??= EnsureGasRow(objectInfo, GetCO2Def());
                    rowCO2.Value += producedCO2;
                }

                // Update totalMoles so photochemistry sees post-combustion state.
                totalMoles -= burnedH2 / H2_MOLAR_MASS;
                totalMoles -= burnedCH4 / CH4_MOLAR_MASS;
                totalMoles -= burnedO2 / O2_MOLAR_MASS;
                totalMoles += producedH2O / H2O_MOLAR_MASS;
                totalMoles += producedCO2 / CO2_MOLAR_MASS;
                if (totalMoles < 0) totalMoles = 0;

                Plugin.Log.LogInfo(
                    $"[Combust] {objectInfo.ObjectName}: " +
                    $"τ={tau:F3} burn={burnFraction:P1} fReactive={fReactive:P1} " +
                    $"Tmax={maxTempK:F0}K Tauto={tAuto:F0}K " +
                    $"flam H₂={h2Flammable}({h2O2Ratio:F2}:1) CH₄={ch4Flammable}({ch4O2Ratio:F2}:1) " +
                    $"→ H₂={burnedH2:F1}t CH₄={burnedCH4:F1}t O₂={burnedO2:F1}t " +
                    $"→ H₂O={producedH2O:F1}t CO₂={producedCO2:F1}t");
            }

            // ── Photochemical oxidation (slow, exponential) ──
            // Recompute pO₂ from actual gas rows (may have been modified by combustion).
            double pO2 = totalMoles > 0
                ? ((rowO2.Value / O2_MOLAR_MASS) / totalMoles) * __instance.pressure
                : 0;
            if (pO2 <= 0) return;

            double distAu = objectInfo.DistanceToSunInAU;
            double starLum = MonoBehaviourSingleton<ObjectInfoManager>
                .Instance.mainObjectInfoSun.StarType.luminosity;
            double uvFactor = starLum / (distAu * distAu)
                * (1.0 + __instance.mirrorsStrength)
                * (1.0 - System.Math.Max(0.0, __instance.shadesStrength));
            if (uvFactor > 100.0) uvFactor = 100.0;

            double h2Mass = rowH2?.Value ?? 0;
            double ch4Mass = rowCH4?.Value ?? 0;
            double h2oMass = rowH2O?.Value ?? 0;

            double h2oMoleFrac = totalMoles > 0
                ? (h2oMass / H2O_MOLAR_MASS) / totalMoles
                : 0;
            double h2oSat = System.Math.Min(h2oMoleFrac / H2O_MOLE_FRAC_EARTH, 1.0);
            double waterFactor = 1.0 + h2oSat * (WET_DRY_RATIO - 1.0);

            double pO2Rel = pO2 / PO2_EARTH;
            double oxidantFactor = pO2Rel * uvFactor;
            if (oxidantFactor <= 0) return;

            double h2Lifetime = H2_LIFETIME_EARTH / (oxidantFactor * waterFactor / WET_DRY_RATIO);
            double ch4Lifetime = CH4_LIFETIME_EARTH / (oxidantFactor * waterFactor / WET_DRY_RATIO);

            double h2Decay = 1.0 - System.Math.Exp(-TICK_YEARS / h2Lifetime);
            double ch4Decay = 1.0 - System.Math.Exp(-TICK_YEARS / ch4Lifetime);

            double h2Removed = h2Mass * h2Decay;
            double ch4Removed = ch4Mass * ch4Decay;

            // O₂-limited: don't create products from nothing.
            double photoO2Demand = h2Removed * H2_O2_RATIO + ch4Removed * CH4_O2_RATIO;
            double photoO2Avail = rowO2?.Value ?? 0;
            if (photoO2Avail <= 0)
            {
                h2Removed = 0;
                ch4Removed = 0;
            }
            else if (photoO2Demand > photoO2Avail)
            {
                double frac = photoO2Avail / photoO2Demand;
                h2Removed *= frac;
                ch4Removed *= frac;
            }

            double photoO2 = h2Removed * H2_O2_RATIO + ch4Removed * CH4_O2_RATIO;
            double photoH2O = h2Removed * H2_H2O_RATIO + ch4Removed * CH4_H2O_RATIO;
            double photoCO2 = ch4Removed * CH4_CO2_RATIO;

            if (rowH2 != null) rowH2.Value = System.Math.Max(0, rowH2.Value - h2Removed);
            if (rowCH4 != null) rowCH4.Value = System.Math.Max(0, rowCH4.Value - ch4Removed);
            if (photoO2 > 0 && rowO2 != null) rowO2.Value = System.Math.Max(0, rowO2.Value - photoO2);
            if (photoH2O > 0)
            {
                if (rowH2O == null) rowH2O = EnsureGasRow(objectInfo, GetWaterDef());
                rowH2O.Value += photoH2O;
            }
            if (photoCO2 > 0)
            {
                if (rowCO2 == null) rowCO2 = EnsureGasRow(objectInfo, GetCO2Def());
                rowCO2.Value += photoCO2;
            }

            Plugin.Log.LogInfo(
                $"[PhotoChem] {objectInfo.ObjectName}: " +
                $"pO₂={pO2:F3}atm UV×{uvFactor:F1} " +
                $"H₂O={h2oMoleFrac*100:F2}% wet×{waterFactor:F1} " +
                $"τ_H₂={h2Lifetime:F1}yr τ_CH₄={ch4Lifetime:F1}yr " +
                $"removed H₂={h2Removed:G3}t CH₄={ch4Removed:G3}t " +
                $"→ H₂O={photoH2O:G3}t CO₂={photoCO2:G3}t");
        }
    }

    /// <summary>
    /// Track pre-warm state to skip photochemistry during save load.
    /// </summary>
    [HarmonyPatch(typeof(ObjectInfo), "Manager.ISaveStateDataProvider.AfterLoadState")]
    class Patch_AfterLoadState_Photochemistry
    {
        static void Prefix() => Patch_UpdatePhotochemistry.IsPreWarming = true;
        static void Postfix() => Patch_UpdatePhotochemistry.IsPreWarming = false;
    }
}

using System;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Data;
using Game.Info;
using Game.ObjectInfoDataScripts.CustomFacilitiesAndModules;
using Game.UI.Windows.Elements.MirrorTargetElements;
using Game.UI.Windows.Windows;
using HarmonyLib;
using Manager;
using TMPro;
using UnityEngine;

#pragma warning disable IDE0051

namespace AlternativeHabitabilityModel
{
    /// <summary>
    /// Storage for fractional mirror allocation. The key is the MirrorTargetInfo
    /// instance (stable identity within a session). Value is the decimal unused
    /// portion (ceil - user_input).
    /// </summary>
    static class MirrorFraction
    {
        public const long LOW_MASK = 0x00000000FFFFFFFFL;
        public const long SCALE    = 1_000_000L;

        public static readonly ConditionalWeakTable<SpaceMirrorOrShadeFacility.MirrorTargetInfo, StrongBox<decimal>>
            Unused = new();
    }

    // ────────────────────────────────────────────────────────────────────
    // Patch 1: Load — extract fraction from high bits before base overwrites
    // ────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(SpaceMirrorOrShadeFacility), nameof(SpaceMirrorOrShadeFacility.OnAfterLoadSave))]
    static class Patch_OnAfterLoadSave_Mirror
    {
        static void Prefix(SpaceMirrorOrShadeFacility __instance)
        {
            if (!__instance.IsMirror()) return;

            foreach (var target in __instance.Targets)
            {
                int unusedMillionths = (int)((ulong)target.mirrorsCount >> 32);
                if (unusedMillionths > 0)
                {
                    MirrorFraction.Unused.Remove(target);
                    MirrorFraction.Unused.Add(target,
                        new StrongBox<decimal>(unusedMillionths / (decimal)MirrorFraction.SCALE));
                }
                target.mirrorsCount &= MirrorFraction.LOW_MASK;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Patch 2: Save — encode fraction into high bits, survives Serialize()
    // ────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(LoadSaveManager), nameof(LoadSaveManager.SaveToFile))]
    static class Patch_SaveToFile_Mirror
    {
        static void Prefix()
        {
            var oim = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (oim?.allObjectInfos == null) return;

            foreach (var oi in oim.allObjectInfos)
            {
                if (oi?.ObjectsInfoData == null) continue;

                foreach (var oid in oi.ObjectsInfoData)
                {
                    if (oid?.ProductionItem == null) continue;

                    foreach (var pi in oid.ProductionItem)
                    {
                        if (pi is SpaceMirrorOrShadeFacility fac && fac.IsMirror())
                        {
                            foreach (var target in fac.Targets)
                            {
                                if (MirrorFraction.Unused.TryGetValue(target, out var box) && box.Value > 0m)
                                {
                                    int unusedMillionths = (int)(box.Value * MirrorFraction.SCALE);
                                    target.mirrorsCount &= MirrorFraction.LOW_MASK;
                                    target.mirrorsCount |= (long)unusedMillionths << 32;
                                }
                            }
                        }
                    }
                }
            }
        }

        static void Postfix()
        {
            var oim = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (oim?.allObjectInfos == null) return;

            foreach (var oi in oim.allObjectInfos)
            {
                if (oi?.ObjectsInfoData == null) continue;

                foreach (var oid in oi.ObjectsInfoData)
                {
                    if (oid?.ProductionItem == null) continue;

                    foreach (var pi in oid.ProductionItem)
                    {
                        if (pi is SpaceMirrorOrShadeFacility facility)
                        {
                            foreach (var target in facility.Targets)
                            {
                                target.mirrorsCount &= MirrorFraction.LOW_MASK;
                            }
                        }
                    }
                }
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Patch 3: UI input — intercept decimal, store fraction, pass integer
    // ────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(MirrorTargetingWindow), "MirrorsInputFieldEndEdit")]
    static class Patch_MirrorsInputFieldEndEdit
    {
        static void Prefix(MirrorTargetingWindow __instance, MirrorTargetRow row, ref string value)
        {
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal parsed))
            {
                return; // let original long.TryParse handle the fallback
            }

            long ceilVal = (long)Math.Ceiling(parsed);
            decimal unused = Math.Max(0m, ceilVal - parsed);

            MirrorFraction.Unused.Remove(row.TargetInfo);
            if (unused > 0m && ceilVal > 0L)
                MirrorFraction.Unused.Add(row.TargetInfo, new StrongBox<decimal>(unused));

            value = ceilVal.ToString(); // original sees an integer
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Patch 4: UI display — show decimal if fractional, allow decimal input
    // ────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(MirrorTargetRow), nameof(MirrorTargetRow.SetData))]
    static class Patch_MirrorTargetRow_SetData
    {
        static void Postfix(MirrorTargetRow __instance)
        {
            var input = __instance.MirrorsInputField;
            input.characterValidation = TMP_InputField.CharacterValidation.Decimal;

            if (MirrorFraction.Unused.TryGetValue(__instance.TargetInfo, out var box))
            {
                input.text =
                    (__instance.TargetInfo.mirrorsCount - box.Value).ToString("0.######");
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Patch 5: Strength calculation (modified existing — fractional aware)
    // ────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(SpaceMirrorOrShadeFacility), "GetFinalStrengthForObject", new[] { typeof(ObjectInfo) })]
    class Patch_GetFinalStrengthForObject
    {

        /// <summary>
        /// Walk up the parentObjectInfo chain to find the top-level planet.
        /// Stops at bodies whose parent is a star (objectTypes=Other).
        /// </summary>
        static ObjectInfo GetSystemRoot(ObjectInfo obj)
        {
            if (obj == null) return null;
            for (;;)
            {
                var parent = obj.parentObjectInfo;
                if (parent == null)
                    return obj;

                // Check if parent is a star
                if (parent.objectTypes == EObjectTypes.Other)
                    return obj;

                obj = parent;
            }
        }

        static bool Prefix(SpaceMirrorOrShadeFacility __instance, ObjectInfo objectInfo, ref double? __result)
        {
            // Only affect mirrors, not shades
            if (!__instance.IsMirror()) return true;

            // Find the target entry — null means this object isn't a valid target
            var mirrorTargetInfo = __instance.Targets.FirstOrDefault(t => t.target.Object == objectInfo);
            if (mirrorTargetInfo == null)
            {
                UnityEngine.Debug.LogError($"[AltMirror] Object {objectInfo.ObjectName} is not a target for {__instance.ObjectInfoData.ObjectInfo.ObjectName}");
                objectInfo.SpaceMirrorsAndShadesTargetingThisObject.Remove(__instance);
                __result = null;
                return false;
            }

            double allocatedCount = mirrorTargetInfo.allocatedCount;
            if (MirrorFraction.Unused.TryGetValue(mirrorTargetInfo, out var unusedBox))
                allocatedCount -= (double)unusedBox.Value;

            if (allocatedCount <= 0) { __result = 0.0; return false; }

            string origin = __instance.ObjectInfoData?.ObjectInfo?.ObjectName ?? "?";

            // ── System check
            ObjectInfo mirrorPlanet = __instance.ObjectInfoData?.ObjectInfo?.parentObjectInfo;
            ObjectInfo mirrorRoot = GetSystemRoot(mirrorPlanet);
            ObjectInfo targetRoot = GetSystemRoot(objectInfo);
            if (mirrorRoot == null || targetRoot == null || mirrorRoot != targetRoot)
            {
                Plugin.Log?.LogDebug($"[AltMirror] {origin}→{objectInfo.ObjectName} sys mismatch {mirrorRoot?.ObjectName ?? "?"}≠{targetRoot?.ObjectName ?? "?"} — skip");
                __result = 0.0;
                return false;
            }

            // ── Planet radius for AltMirror formula ───────────────────────────
            double radiusM = objectInfo.Radius;

            // Mirror area: MirrorAreaMkm2 million km² → m²
            double A_mirror = Plugin.MirrorAreaMkm2.Value * 1e12;

            // AltMirror strength: A / (π × R²) per allocated station
            double alt = A_mirror / (Math.PI * radiusM * radiusM) * allocatedCount;
            __result = alt;
            return false;
        }
    }
}

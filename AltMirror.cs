using System;
using System.Linq;
using Data;
using Game.Info;
using Game.ObjectInfoDataScripts.CustomFacilitiesAndModules;
using HarmonyLib;

#pragma warning disable IDE0051

namespace AlternativeHabitabilityModel
{
    /// <summary>
    /// Harmony patch that replaces the vanilla mirror strength formula with
    /// a physically-grounded planet-orbiting mirror model (Regime B, f_coupling=1).
    ///
    /// Vanilla: strength = specialAbilityParameter / (d_mirror² × d_diff²) × count
    /// AltMirror: strength = A_mirror / (π × R_planet²) × count
    ///
    /// Shades are left unchanged.
    /// </summary>
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

            long allocatedCount = mirrorTargetInfo.allocatedCount;

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

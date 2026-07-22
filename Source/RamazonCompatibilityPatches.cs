using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Ramazon
{
    [StaticConstructorOnStartup]
    public static class RamazonCompatibilityPatches
    {
        static RamazonCompatibilityPatches()
        {
            new Harmony("heinrikdev.ramazon.compatibility").PatchAll();
        }

        public static void EnsureMapRenderGrids(Map map)
        {
            if (map == null) return;

            EnsureGrid(map, "snowGrid", "Verse.SnowGrid");
            EnsureGrid(map, "sandGrid", "Verse.SandGrid");
            EnsureGrid(map, "pollutionGrid", "Verse.PollutionGrid");
            EnsureGrid(map, "fogGrid", "Verse.FogGrid");
            EnsureGrid(map, "terrainGrid", "Verse.TerrainGrid");
        }

        public static bool CanRegenerateMapMesh(Map map)
        {
            if (map == null) return false;

            if (IsDisposed(map))
            {
                Log.WarningOnce("[Ramazon] Skipped mesh regeneration for a disposed map. This works around a RimWorld Together map removal crash on macOS.", 78123020);
                return false;
            }

            EnsureMapRenderGrids(map);
            return true;
        }

        private static bool IsDisposed(Map map)
        {
            FieldInfo disposedField = AccessTools.Field(typeof(Map), "disposed");
            if (disposedField?.GetValue(map) is bool disposedValue) return disposedValue;

            PropertyInfo disposedProperty = AccessTools.Property(typeof(Map), "Disposed");
            if (disposedProperty?.GetValue(map, null) is bool disposedPropertyValue) return disposedPropertyValue;

            return false;
        }

        private static void EnsureGrid(Map map, string fieldName, string typeName)
        {
            FieldInfo field = AccessTools.Field(typeof(Map), fieldName);
            if (field == null || field.GetValue(map) != null) return;

            Type gridType = AccessTools.TypeByName(typeName);
            if (gridType == null) return;

            object grid = Activator.CreateInstance(gridType, map);
            field.SetValue(map, grid);
            Log.WarningOnce($"[Ramazon] Recreated missing {fieldName} before map mesh regeneration. This works around a RimWorld Together load-order crash on macOS.", 78123000 + fieldName.GetHashCode());
        }
    }

    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.RegenerateEverythingNow))]
    public static class Patch_MapDrawer_RegenerateEverythingNow
    {
        private static readonly FieldInfo MapField = AccessTools.Field(typeof(MapDrawer), "map");

        public static bool Prefix(MapDrawer __instance)
        {
            var map = MapField?.GetValue(__instance) as Map;
            return RamazonCompatibilityPatches.CanRegenerateMapMesh(map);
        }
    }

    [HarmonyPatch(typeof(Section), nameof(Section.RegenerateAllLayers))]
    public static class Patch_Section_RegenerateAllLayers
    {
        private static readonly FieldInfo MapField = AccessTools.Field(typeof(Section), "map");

        public static bool Prefix(Section __instance)
        {
            var map = MapField?.GetValue(__instance) as Map;
            return RamazonCompatibilityPatches.CanRegenerateMapMesh(map);
        }
    }

    [HarmonyPatch(typeof(GridsUtility), nameof(GridsUtility.GetSnowDepth))]
    public static class Patch_GridsUtility_GetSnowDepth
    {
        public static bool Prefix(IntVec3 c, Map map, ref float __result)
        {
            if (map?.snowGrid != null) return true;

            __result = 0f;
            Log.WarningOnce("[Ramazon] Returned 0 snow depth because map.snowGrid was missing during rendering.", 78123010);
            return false;
        }
    }

    [HarmonyPatch(typeof(GridsUtility), nameof(GridsUtility.IsPolluted))]
    public static class Patch_GridsUtility_IsPolluted
    {
        public static bool Prefix(IntVec3 c, Map map, ref bool __result)
        {
            if (map?.pollutionGrid != null) return true;

            __result = false;
            Log.WarningOnce("[Ramazon] Returned false for pollution because map.pollutionGrid was missing during rendering.", 78123011);
            return false;
        }
    }
}

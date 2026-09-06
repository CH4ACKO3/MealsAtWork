using HarmonyLib;
using UnityEngine;
using Verse;

namespace DeskLunch
{
    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAndApparelExtras))]
    static class LunchDrawing
    {
        static void Postfix(Pawn pawn, PawnRenderFlags flags)
        {
            if ((flags & (PawnRenderFlags.Portrait | PawnRenderFlags.Statue | PawnRenderFlags.Invisible | PawnRenderFlags.Cache)) != 0
                || pawn?.jobs?.curDriver == null || !LunchUtility.HasActiveMeal(pawn.jobs.curDriver)) return;
            Thing station = pawn.CurJob.targetA.Thing;
            if (!station.Spawned || station.Position.Fogged(station.Map)) return;
            // Like vanilla table eating: draw over the nearest surface cell. Keep the
            // real meal in inventory so it cannot be hauled away or used as an ingredient.
            CellRect surface = station.OccupiedRect();
            var cell = new IntVec3(Mathf.Clamp(pawn.Position.x, surface.minX, surface.maxX), 0,
                Mathf.Clamp(pawn.Position.z, surface.minZ, surface.maxZ));
            Vector3 location = cell.ToVector3ShiftedWithAltitude(AltitudeLayer.ItemImportant);
            Thing meal = LunchUtility.State(pawn.jobs.curDriver).meal;
            Graphic graphic = meal.Graphic;
            if (graphic is Graphic_StackCount stackGraphic) graphic = stackGraphic.SubGraphicForStackCount(1, meal.def);
            graphic.Draw(location, Rot4.North, meal);
        }
    }
}

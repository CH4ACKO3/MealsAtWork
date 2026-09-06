using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DeskLunch
{
    public static class IdleWorktableDining
    {
        public static bool EmptyForDining(Building_WorkTable table) => table?.BillStack != null && table.BillStack.Count == 0;
        public static bool PreferNormalTable(float normalDistanceSquared, float worktableDistanceSquared, float weight)
            => normalDistanceSquared <= worktableDistanceSquared * Mathf.Max(1f, weight) * Mathf.Max(1f, weight);

        public static WorkMealSession Current(Pawn pawn)
        {
            var manager = LunchBreakUtility.Manager(pawn);
            var record = manager?.Get(pawn);
            return record?.diningStation != null && manager.DiningValid(record) && pawn.Position == record.diningCell ? record : null;
        }

        public static IntVec3 SurfaceCell(Thing table, IntVec3 seat)
        {
            CellRect rect = table.OccupiedRect();
            return new IntVec3(Mathf.Clamp(seat.x, rect.minX, rect.maxX), 0, Mathf.Clamp(seat.z, rect.minZ, rect.maxZ));
        }

        public static bool TryStart(Pawn pawn, Thing food, bool startPath = true)
        {
            if (!DeskLunchMod.Settings.idleWorktables || !MealWorker.Supported(pawn) || pawn.Drafted || pawn.Downed
                || !(pawn.jobs.curDriver is JobDriver_Ingest) || food?.def.ingestible == null) return false;
            float radius = food.def.ingestible.chairSearchRadius;
            if (radius <= 0 || !food.def.ingestible.tableDesired) return false;
            bool normalFound = Toils_Ingest.TryFindChairOrSpot(pawn, food, out var normalCell);
            bool properTable = normalFound && GenAdj.CardinalDirections.Any(d => (normalCell + d).InBounds(pawn.Map) && (normalCell + d).HasEatSurface(pawn.Map));
            float maxDistance = radius * radius;
            var candidates = pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial).OfType<Building_WorkTable>()
                .Where(t => t.def.hasInteractionCell && EmptyForDining(t))
                .OrderBy(t => (t.InteractionCell - pawn.Position).LengthHorizontalSquared);
            foreach (var table in candidates)
            {
                IntVec3 seat = table.InteractionCell;
                int distance = (seat - pawn.Position).LengthHorizontalSquared;
                if (distance > maxDistance) continue;
                if (properTable && PreferNormalTable((normalCell - pawn.Position).LengthHorizontalSquared,
                    distance, DeskLunchMod.Settings.worktableDistanceWeight)) continue;
                if (table.Faction != pawn.Faction || table.IsForbidden(pawn) || !table.IsSociallyProper(pawn) || table.IsBurning()
                    || !seat.InBounds(pawn.Map) || !seat.Standable(pawn.Map) || seat.Fogged(pawn.Map)
                    || seat.GetDangerFor(pawn, pawn.Map) != Danger.None || !pawn.CanReserve(table)
                    || !pawn.CanReserveSittableOrSpot(seat) || !pawn.CanReach(seat, PathEndMode.OnCell, Danger.None)) continue;
                // Claim before moving. A newly added bill cannot race the meal for this station.
                if (!pawn.Reserve(table, pawn.CurJob, errorOnFailed: false)) continue;
                if (!pawn.ReserveSittableOrSpot(seat, pawn.CurJob, errorOnFailed: false))
                {
                    pawn.Map.reservationManager.Release(table, pawn, pawn.CurJob);
                    continue;
                }
                var manager = LunchBreakUtility.Manager(pawn);
                var record = manager.Get(pawn);
                if (record == null)
                {
                    record = new WorkMealSession { pawn = pawn, diningOnly = true, workJobId = -1,
                        mealJobId = pawn.CurJob.loadID, deadline = Find.TickManager.TicksGame + 7500 };
                    manager.Add(record);
                }
                record.diningStation = table;
                record.diningCell = seat;
                pawn.Map.pawnDestinationReservationManager.Reserve(pawn, pawn.CurJob, seat);
                if (startPath) pawn.pather.StartPath(seat, PathEndMode.OnCell);
                return true;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Toils_Ingest), nameof(Toils_Ingest.CarryIngestibleToChewSpot))]
    static class IdleTableSpotPatch
    {
        static void Postfix(Toil __result, TargetIndex ingestibleInd)
        {
            var toil = __result;
            var normal = toil.initAction;
            toil.initAction = () =>
            {
                if (!IdleWorktableDining.TryStart(toil.actor, toil.actor.CurJob.GetTarget(ingestibleInd).Thing)) normal();
            };
        }
    }

    [HarmonyPatch(typeof(Toils_Ingest), nameof(Toils_Ingest.FindAdjacentEatSurface))]
    static class IdleTableSurfacePatch
    {
        static void Postfix(Toil __result, TargetIndex eatSurfaceInd)
        {
            var toil = __result;
            var normal = toil.initAction;
            toil.initAction = () =>
            {
                var record = IdleWorktableDining.Current(toil.actor);
                if (record == null) { normal(); return; }
                toil.actor.CurJob.SetTarget(eatSurfaceInd, IdleWorktableDining.SurfaceCell(record.diningStation, record.diningCell));
                toil.actor.jobs.curDriver.rotateToFace = eatSurfaceInd;
            };
        }
    }

    [HarmonyPatch(typeof(JobDriver_Ingest), nameof(JobDriver_Ingest.ModifyCarriedThingDrawPos))]
    static class IdleTablePlatePatch
    {
        static void Postfix(JobDriver_Ingest __instance, ref Vector3 drawPos, ref bool flip, ref bool __result)
        {
            var record = IdleWorktableDining.Current(__instance.pawn);
            if (record == null) return;
            drawPos = IdleWorktableDining.SurfaceCell(record.diningStation, record.diningCell).ToVector3ShiftedWithAltitude(AltitudeLayer.ItemImportant);
            flip = false;
            __result = true;
        }
    }

    [HarmonyPatch(typeof(Toils_Ingest), nameof(Toils_Ingest.FinalizeIngest))]
    static class IdleTableFinishPatch
    {
        static void Postfix(Toil __result, Pawn ingester, TargetIndex ingestibleInd)
        {
            var toil = __result;
            var normal = toil.initAction;
            toil.initAction = () =>
            {
                var record = IdleWorktableDining.Current(ingester);
                if (record == null) { normal(); return; }
                Job job = toil.actor.CurJob;
                Thing food = job.GetTarget(ingestibleInd).Thing;
                float wanted = ingester.needs.food.NutritionWanted;
                if (job.ingestTotalCount) wanted = food.GetStatValue(StatDefOf.Nutrition) * food.stackCount;
                else if (job.overeat) wanted = Mathf.Max(wanted, 0.75f);
                // A workshop's impressiveness is not a dining-room benefit.
                if (ingester.needs.mood != null && food.def.IsNutritionGivingIngestible && food.def.ingestible.chairSearchRadius > 10f)
                {
                    Room room = ingester.GetRoom();
                    if (room?.Role?.defName == "DiningRoom")
                    {
                        int stage = RoomStatDefOf.Impressiveness.GetScoreStageIndex(room.GetStat(RoomStatDefOf.Impressiveness));
                        if (ThoughtDefOf.AteInImpressiveDiningRoom.stages[stage] != null)
                            ingester.needs.mood.thoughts.memories.TryGainMemory(ThoughtMaker.MakeThought(ThoughtDefOf.AteInImpressiveDiningRoom, stage));
                    }
                }
                LunchUtility.FinishMeal(ingester, food, wanted);
                LunchBreakUtility.Manager(ingester)?.EndDining(record);
            };
        }
    }
}

using System;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeskLunch
{
    public static class MealCouriers
    {
        public static float FreeVolume(Pawn pawn) => System.Math.Max(0, pawn.GetStatValue(StatDefOf.CarryingCapacity)
            - pawn.inventory.innerContainer.Sum(t => t.stackCount * t.def.VolumePerUnit));
        public static int PickupLimit(Pawn pawn, Thing food)
        {
            int volumeLimit = (int)System.Math.Floor(FreeVolume(pawn) / System.Math.Max(0.0001f, food.def.VolumePerUnit));
            return System.Math.Min(pawn.carryTracker.MaxStackSpaceEver(food.def), MassUtility.CanEverCarryAnything(pawn)
                ? System.Math.Min(volumeLimit, MassUtility.CountToPickUpUntilOverEncumbered(pawn, food)) : volumeLimit);
        }
        // Reuse the game's work-giver gate, including compatibility patches applied to it.
        static readonly Func<JobGiver_Work, Pawn, WorkGiver, bool> CanUseWorkGiver =
            AccessTools.MethodDelegate<Func<JobGiver_Work, Pawn, WorkGiver, bool>>(
                AccessTools.Method(typeof(JobGiver_Work), "PawnCanUseWorkGiver"));

        public static bool HasCourier(Pawn receiver) => receiver?.Map != null
            && receiver.Map.mapPawns.AllPawnsSpawned.Any(p => CanServe(p, receiver));

        public static bool CanServe(Pawn pawn, Pawn receiver)
        {
            if (pawn == null || receiver == null || pawn == receiver || !pawn.Spawned || !receiver.Spawned
                || pawn.Map != receiver.Map || pawn.Faction != Faction.OfPlayer
                || pawn.Dead || pawn.Downed || pawn.Drafted || pawn.InMentalState
                || pawn.IsPrisoner || pawn.carryTracker == null || pawn.inventory == null || pawn.thinker == null
                || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving)
                || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return false;
            var root = pawn.thinker.MainThinkNodeRoot;
            if (root == null) return false;
            if (pawn.RaceProps.IsMechanoid && (!pawn.IsColonyMechPlayerControlled
                || pawn.GetMechWorkMode() != MechWorkModeDefOf.Work)) return false;

            // Animals use their trained hauling branch; mod workers may use the work-settings branch.
            bool canSchedule = pawn.IsAnimal && pawn.training?.HasLearned(DefDatabase<TrainableDef>.GetNamed("Haul")) == true
                && root.ThisAndChildrenRecursive.OfType<JobGiver_Haul>().Any();
            if (!canSchedule && pawn.workSettings?.EverWork == true)
            {
                var node = root.ThisAndChildrenRecursive.OfType<JobGiver_Work>().FirstOrDefault(n => !n.emergency);
                canSchedule = node != null && pawn.workSettings.WorkGiversInOrderNormal
                    .Any(g => g is WorkGiver_DeliverMeal && CanUseWorkGiver(node, pawn, g));
            }
            return canSchedule && !receiver.IsForbidden(pawn)
                && pawn.CanReach(receiver, PathEndMode.Touch, Danger.None);
        }
    }

    // This runs only when the animal's own think tree has admitted its hauling behavior.
    [HarmonyPatch(typeof(JobGiver_Haul), "TryGiveJob")]
    static class AnimalMealDeliveryPatch
    {
        static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (!pawn.IsAnimal || pawn.training?.HasLearned(DefDatabase<TrainableDef>.GetNamed("Haul")) != true) return true;
            var manager = MealDelivery.For(pawn);
            if (manager == null || manager.orders.Count == 0) return true;
            var giver = DefDatabase<WorkGiverDef>.GetNamed("MealsAtWork_DeliverMeal").Worker as WorkGiver_DeliverMeal;
            if (giver == null) return true;
            foreach (var order in manager.orders)
            {
                var job = giver.JobOnThing(pawn, order.receiver);
                if (job == null) continue;
                job.workGiverDef = giver.def;
                __result = job;
                return false;
            }
            return true;
        }
    }
}

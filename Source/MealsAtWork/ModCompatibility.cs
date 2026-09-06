using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeskLunch
{
    // Optional integrations: no compile-time dependency or mutation of another mod's tags.
    public static class MealCargo
    {
        private static readonly System.Type haulComp = AccessTools.TypeByName("PickUpAndHaul.CompHauledToInventory");
        private static readonly FieldInfo hauled = haulComp == null ? null : AccessTools.Field(haulComp, "takenToInventory");
        private static readonly System.Type unloadComp = AccessTools.TypeByName("CommonSense.CompUnloadChecker");
        private static readonly FieldInfo unload = unloadComp == null ? null : AccessTools.Field(unloadComp, "ShouldUnload");

        public static bool IsCargo(Pawn pawn, Thing food)
        {
            if (pawn == null || food == null) return false;
            if (hauled != null && pawn.AllComps != null)
            {
                var comp = pawn.AllComps.FirstOrDefault(c => haulComp.IsInstanceOfType(c));
                if (comp != null && hauled.GetValue(comp) is ICollection<Thing> items && items.Contains(food)) return true;
            }
            if (unload != null && food is ThingWithComps thing && thing.AllComps != null)
            {
                var comp = thing.AllComps.FirstOrDefault(c => unloadComp.IsInstanceOfType(c));
                if (comp != null && (bool)unload.GetValue(comp)) return true;
            }
            return false;
        }

        public static bool TransferDelivery(Pawn courier, Pawn recipient, Thing food)
        {
            // A merge with a hauled stack would inherit the destination stack's cargo tag.
            return courier.carryTracker.innerContainer.TryTransferToContainer(food,
                recipient.inventory.innerContainer, 1, canMergeWithExistingStacks: false) == 1;
        }
    }

    [HarmonyPatch]
    static class CommonSenseMealSpotPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(
            "CommonSense.JobDriver_PrepareToIngestToils_ToolUser_CommonSensePatch:ReserveChewSpot");
        private static bool Prepare() => TargetMethod() != null;
        private static void Postfix(Toil __result, TargetIndex ingestibleInd, TargetIndex StoreToInd)
        {
            var toil = __result;
            var original = toil.initAction;
            toil.initAction = () =>
            {
                Pawn p = toil.actor;
                if (!IdleWorktableDining.TryStart(p, p.CurJob.GetTarget(ingestibleInd).Thing, startPath: false))
                { original(); return; }
                // CS will clean this destination and then walk there using its own toils.
                p.CurJob.SetTarget(StoreToInd, LunchBreakUtility.Manager(p).Get(p).diningCell);
            };
        }
    }
}

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeskLunch
{
    [HarmonyPatch(typeof(Pawn_JobTracker), "DetermineNextJob")]
    static class HungryNextWorkPatch
    {
        static void Postfix(Pawn ___pawn, ref ThinkResult __result, ref ThinkTreeDef thinkTree)
        {
            Pawn p = ___pawn;
            var manager = MealDelivery.For(p);
            if (!(__result.SourceNode is JobGiver_GetFood) || __result.FromQueue
                || __result.Job?.playerForced == true || manager == null || !manager.CanChooseWork(p)) return;
            // Ask the actual work node once, preserving its work priority and result metadata.
            var node = p.thinker.MainThinkNodeRoot.ThisAndChildrenRecursive
                .OfType<JobGiver_Work>().FirstOrDefault(n => !n.emergency);
            if (node == null) return;
            ThinkResult work = node.TryIssueJobPackage(p, new JobIssueParams { ignoreQueue = true });
            if (!MealDelivery.WorkJob(work.Job))
            {
                if (work.Job != null) JobMaker.ReturnToPool(work.Job);
                return;
            }
            JobMaker.ReturnToPool(__result.Job);
            __result = work;
            thinkTree = p.thinker.MainThinkTree;
            manager.Request(p);
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.JobTrackerTickInterval))]
    static class HungryWorkStagePatch
    {
        static void Prefix(Pawn ___pawn) => MealDelivery.For(___pawn)?.Observe(___pawn);
    }

    [HarmonyPatch(typeof(Toils_Recipe), nameof(Toils_Recipe.DoRecipeWork))]
    static class RecipePatch
    {
        static void Postfix(Toil __result) => LunchUtility.EnableFor(__result);
    }

    [HarmonyPatch(typeof(JobDriver_Research), "MakeNewToils")]
    static class ResearchPatch
    {
        static IEnumerable<Toil> Postfix(IEnumerable<Toil> __result)
        {
            foreach (Toil toil in __result)
            {
                if (toil.tickIntervalAction != null && toil.activeSkill != null)
                {
                    LunchUtility.EnableFor(toil);
                    // Preserve the other failure conditions (destroyed/forbidden bench,
                    // unreachable interaction cell) and all external interruptions.
                    for (int i = 0; i < System.Math.Min(2, toil.endConditions.Count); i++)
                    {
                        var original = toil.endConditions[i];
                        toil.endConditions[i] = () => LunchUtility.FinishResearchLunch(toil.actor) ? JobCondition.Ongoing : original();
                    }
                }
                yield return toil;
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.ReadyForNextToil))]
    static class WorkCompletionPatch
    {
        static bool Prefix(JobDriver __instance) => !LunchUtility.DeferWorkCompletion(__instance);
    }

    [HarmonyPatch(typeof(Toil), nameof(Toil.Clear))]
    static class ToilPoolPatch
    {
        static void Prefix(Toil __instance) => LunchUtility.Forget(__instance);
    }

    [HarmonyPatch(typeof(JobGiver_GetFood), nameof(JobGiver_GetFood.GetPriority))]
    static class FoodPriorityPatch
    {
        static void Postfix(Pawn pawn, ref float __result)
        {
            if (__result > 0f && (LunchUtility.CanEatAtWork(pawn) || MealDelivery.ShouldWait(pawn))) __result = 0f;
        }
    }

    [HarmonyPatch(typeof(JobGiver_GetFood), "TryGiveJob")]
    static class FoodJobPatch
    {
        static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (!LunchUtility.CanEatAtWork(pawn) && !MealDelivery.ShouldWait(pawn)) return true;
            __result = null;
            return false;
        }
    }

    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.ExposeData))]
    static class SavePatch
    {
        static void Postfix(JobDriver __instance) => LunchUtility.State(__instance).ExposeData();
    }

    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.GetReport))]
    static class ReportPatch
    {
        static void Postfix(JobDriver __instance, ref string __result) => LunchUtility.AppendReport(__instance, ref __result);
    }

    [HarmonyPatch(typeof(JobDriver_DoBill), nameof(JobDriver_DoBill.GetReport))]
    static class BillReportPatch
    {
        static void Postfix(JobDriver_DoBill __instance, ref string __result) => LunchUtility.AppendReport(__instance, ref __result);
    }
}

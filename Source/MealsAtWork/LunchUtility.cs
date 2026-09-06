using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DeskLunch
{
    public sealed class LunchState
    {
        public Thing meal;
        public int remaining;
        public int duration;
        public int remainder;
        public int toilIndex = -1;
        public bool waitingForMeal;
        public void Reset()
        {
            meal = null;
            remaining = duration = remainder = 0;
            toilIndex = -1;
            waitingForMeal = false;
        }
        public void ExposeData()
        {
            Scribe_References.Look(ref meal, "deskLunchMeal");
            Scribe_Values.Look(ref remaining, "deskLunchRemaining");
            Scribe_Values.Look(ref duration, "deskLunchDuration");
            Scribe_Values.Look(ref remainder, "deskLunchRemainder");
            Scribe_Values.Look(ref toilIndex, "deskLunchToilIndex", -1);
            Scribe_Values.Look(ref waitingForMeal, "deskLunchWaitingForMeal");
        }
    }

    public static class LunchUtility
    {
        private static readonly ConditionalWeakTable<Toil, object> workToils = new ConditionalWeakTable<Toil, object>();
        private static readonly ConditionalWeakTable<JobDriver, LunchState> states = new ConditionalWeakTable<JobDriver, LunchState>();
        private static readonly Func<JobDriver, Toil> CurrentToil = AccessTools.MethodDelegate<Func<JobDriver, Toil>>(AccessTools.PropertyGetter(typeof(JobDriver), "CurToil"));
        public static LunchState State(JobDriver driver) => states.GetOrCreateValue(driver);
        public static void Forget(Toil toil) => workToils.Remove(toil);
        public static bool IsWorkToil(Toil toil) => toil != null && workToils.TryGetValue(toil, out _);
        public static bool IsWorking(Pawn pawn) => pawn?.jobs?.curDriver != null && IsWorkToil(CurrentToil(pawn.jobs.curDriver));

        // Public entry point for other mods with stationary work toils. Does not change job targets.
        public static void EnableFor(Toil toil)
        {
            if (toil.tickIntervalAction == null || workToils.TryGetValue(toil, out _)) return;
            workToils.Add(toil, new object());
            Action<int> work = toil.tickIntervalAction;
            Action workTick = toil.tickAction;
            if (workTick != null)
                toil.tickAction = () => { if (!State(toil.actor.jobs.curDriver).waitingForMeal) workTick(); };
            toil.tickIntervalAction = delta =>
            {
                JobDriver driver = toil.actor.jobs.curDriver;
                bool waiting = State(driver).waitingForMeal;
                int effectiveDelta = Tick(toil.actor, delta, allowStart: !waiting);
                // Ingestion outcome callbacks may end the job (e.g. health changes).
                if (toil.actor.jobs.curDriver != driver || CurrentToil(driver) != toil) return;
                if (waiting)
                {
                    // Keep the original job and reservations until this one meal finishes.
                    // Never invoke recipe/research work again after its completion callback.
                    if (!HasActiveMeal(driver)) driver.ReadyForNextToil();
                }
                else if (effectiveDelta > 0) work(effectiveDelta);
            };
            toil.AddFinishAction(() =>
            {
                LunchBreakUtility.EndAtWork(toil.actor);
                if (toil.actor?.jobs?.curDriver != null) State(toil.actor.jobs.curDriver).Reset();
            });
        }

        public static bool HasActiveMeal(JobDriver driver)
        {
            return driver != null && states.TryGetValue(driver, out var state) && state.meal != null
                && state.remaining > 0 && state.toilIndex == driver.CurToilIndex && AtWork(driver.pawn)
                && Suitable(driver.pawn, state.meal);
        }

        public static bool DeferWorkCompletion(JobDriver driver)
        {
            if (!HasActiveMeal(driver)) return false;
            State(driver).waitingForMeal = true;
            return true;
        }

        // Research completion clears the selected project; its first two vanilla failure
        // predicates otherwise abort chewing before ReadyForNextToil can be reached.
        public static bool FinishResearchLunch(Pawn pawn)
        {
            return Find.ResearchManager.GetProject() == null && DeferWorkCompletion(pawn.jobs.curDriver);
        }

        private static bool AtWork(Pawn pawn)
        {
            if (!DeskLunchMod.Settings.enabled || pawn == null || !pawn.Spawned || !MealWorker.Supported(pawn)
                || pawn.Drafted || pawn.Downed || pawn.InMentalState
                || pawn.needs?.food == null || pawn.needs.food.CurCategory == HungerCategory.Starving
                || pawn.pather.Moving || pawn.jobs?.curDriver == null) return false;
            Thing station = pawn.CurJob.targetA.Thing;
            if (!(station is Building_WorkTable) && !(station is Building_ResearchBench)) return false;
            Toil toil = CurrentToil(pawn.jobs.curDriver);
            return toil != null && workToils.TryGetValue(toil, out _);
        }

        private static bool Suitable(Pawn pawn, Thing food)
        {
            return food != null && !food.Destroyed && pawn.inventory?.innerContainer.Contains(food) == true
                && !MealCargo.IsCargo(pawn, food)
                && food.def.IsNutritionGivingIngestible && !food.def.IsDrug
                && (food.def.ingestible.foodType & FoodTypeFlags.Meal) != 0
                && food.IngestibleNow && !food.IsNotFresh() && pawn.WillEat(food)
                && FoodUtility.NutritionForEater(pawn, food) > 0f;
        }

        private static Thing FindMeal(Pawn pawn)
        {
            if (pawn.inventory == null) return null;
            Thing best = null;
            foreach (Thing food in pawn.inventory.innerContainer)
                if (Suitable(pawn, food) && (best == null || food.def.ingestible.preferability > best.def.ingestible.preferability))
                    best = food;
            return best;
        }

        public static bool CanEatAtWork(Pawn pawn)
        {
            if (DeskLunchMod.Settings.lunchBreaks && DeskLunchMod.Settings.preferLunchBreak && LunchBreakUtility.CanStart(pawn)) return false;
            if (!AtWork(pawn)) return false;
            LunchState state = State(pawn.jobs.curDriver);
            if (state.meal != null && state.toilIndex == pawn.jobs.curDriver.CurToilIndex && Suitable(pawn, state.meal)) return true;
            if (!MealSchedule.AllowsWorkMeals(pawn)) return false;
            return pawn.needs.food.CurLevelPercentage < pawn.RaceProps.FoodLevelPercentageWantEat && FindMeal(pawn) != null;
        }

        private static int Tick(Pawn pawn, int delta, bool allowStart)
        {
            MealDelivery.For(pawn)?.Observe(pawn);
            LunchState state = State(pawn.jobs.curDriver);
            if (!AtWork(pawn)) { LunchBreakUtility.EndAtWork(pawn); state.Reset(); return delta; }
            if (state.meal != null && (state.toilIndex != pawn.jobs.curDriver.CurToilIndex || !Suitable(pawn, state.meal)))
            {
                LunchBreakUtility.EndAtWork(pawn);
                state.Reset();
            }
            if (state.meal == null)
            {
                if (!MealSchedule.AllowsWorkMeals(pawn)) return delta;
                if (DeskLunchMod.Settings.lunchBreaks && DeskLunchMod.Settings.preferLunchBreak && LunchBreakUtility.CanStart(pawn)) return delta;
                if (!allowStart) return delta;
                if (pawn.needs.food.CurLevelPercentage >= pawn.RaceProps.FoodLevelPercentageWantEat) return delta;
                state.meal = FindMeal(pawn);
                if (state.meal == null) return delta;
                float speed = state.meal.def.ingestible.useEatingSpeedStat ? pawn.GetStatValue(StatDefOf.EatingSpeed) : 1f;
                state.duration = state.remaining = Mathf.Max(1, Mathf.RoundToInt(state.meal.def.ingestible.baseIngestTicks / Mathf.Max(0.01f, speed)));
                state.toilIndex = pawn.jobs.curDriver.CurToilIndex;
            }
            LunchBreakUtility.TrackAtWork(pawn);
            int workTicks = LunchTiming.WorkTicks(delta, state.remaining, ref state.remainder);
            state.remaining -= delta;
            if (state.remaining <= 0)
            {
                Thing meal = state.meal;
                LunchBreakUtility.EndAtWork(pawn);
                state.Reset(); // Clear before callbacks: an interrupted job must never consume this twice.
                FinishMeal(pawn, meal, pawn.needs.food.NutritionWanted);
            }
            return workTicks;
        }

        public static bool ShouldApplyPenalty(Pawn pawn) => DeskLunchMod.Settings.moodPenalty
            && pawn?.needs?.mood != null && pawn.story?.traits != null
            && ThoughtUtility.CanGetThought(pawn, ThoughtDefOf.AteWithoutTable, checkIfNullified: true);

        public static void FinishMeal(Pawn pawn, Thing meal, float nutritionWanted)
        {
            float nutrition = meal.Ingested(pawn, nutritionWanted);
            if (!pawn.Dead && pawn.needs?.food != null)
            {
                pawn.needs.food.CurLevel += nutrition;
                pawn.records?.AddTo(RecordDefOf.NutritionEaten, nutrition);
                if (nutrition > 0 && ShouldApplyPenalty(pawn))
                    pawn.needs.mood?.thoughts.memories.TryGainMemory(DefDatabase<ThoughtDef>.GetNamed("DeskLunch_AteWhileWorking"));
            }
        }

        public static void AppendReport(JobDriver driver, ref string report)
        {
            if (!states.TryGetValue(driver, out var state) || state.meal == null || !AtWork(driver.pawn)) return;
            int percent = Mathf.Clamp(Mathf.RoundToInt(100f * (1f - (float)state.remaining / Math.Max(1, state.duration))), 0, 100);
            report = state.waitingForMeal ? "DeskLunch.FinishingReport".Translate(percent) : "DeskLunch.Report".Translate(report, percent);
        }
    }
}

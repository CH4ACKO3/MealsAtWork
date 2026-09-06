using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeskLunch
{
    public sealed class WorkMealSession : IExposable
    {
        public Pawn pawn;
        public Thing station;
        public int workJobId;
        public int mealJobId;
        public int transitionJobId = -1;
        public int deadline;
        public float workLeft;
        public int workTicks;
        public bool ate;
        public bool atWork;
        public bool diningOnly;
        public Thing diningStation;
        public IntVec3 diningCell = IntVec3.Invalid;
        public List<Thing> protectedThings = new List<Thing>();
        public bool AcceptsJob(int? id) => diningOnly ? id == mealJobId : atWork ? id == workJobId
            : id == mealJobId || ate && (id == workJobId || transitionJobId >= 0 && id == transitionJobId);
        public bool AcceptCompletionTransition(Job job)
        {
            // Vanilla inserts exactly one posture tick between successful ingestion and
            // consuming the queue. Record its identity instead of admitting arbitrary waits.
            if (atWork || diningOnly || !ate || transitionJobId >= 0 || job == null || job.playerForced
                || job.def?.defName != "Wait_MaintainPosture" || job.expiryInterval != 1) return false;
            transitionJobId = job.loadID;
            return true;
        }
        public void RestoreProgress(JobDriver_DoBill driver)
        {
            driver.workLeft = workLeft;
            driver.ticksSpentDoingRecipeWork = workTicks;
            if (driver.job.targetB.Thing is UnfinishedThing uft) uft.workLeft = workLeft;
        }
        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_References.Look(ref station, "station");
            Scribe_Values.Look(ref workJobId, "workJobId");
            Scribe_Values.Look(ref mealJobId, "mealJobId");
            Scribe_Values.Look(ref transitionJobId, "transitionJobId", -1);
            Scribe_Values.Look(ref deadline, "deadline");
            Scribe_Values.Look(ref workLeft, "workLeft");
            Scribe_Values.Look(ref workTicks, "workTicks");
            Scribe_Values.Look(ref ate, "ate");
            Scribe_Values.Look(ref atWork, "atWork");
            Scribe_Values.Look(ref diningOnly, "diningOnly");
            Scribe_References.Look(ref diningStation, "diningStation");
            Scribe_Values.Look(ref diningCell, "diningCell", IntVec3.Invalid);
            Scribe_Collections.Look(ref protectedThings, "protectedThings", LookMode.Reference);
        }
    }

    public sealed class WorkMealReservations : MapComponent
    {
        private List<WorkMealSession> breaks = new List<WorkMealSession>();
        public WorkMealReservations(Map map) : base(map) { }
        public WorkMealSession Get(Pawn pawn) => breaks.FirstOrDefault(r => r.pawn == pawn);
        public WorkMealSession Protecting(Thing thing) => breaks.FirstOrDefault(r => r.protectedThings.Contains(thing));
        public void Add(WorkMealSession record) => breaks.Add(record);
        public void Resumed(WorkMealSession record) => breaks.Remove(record);
        public bool DiningValid(WorkMealSession r)
        {
            // Empty bills are an admission rule only. New bills never revoke a meal.
            return r.diningStation != null && r.diningStation.Spawned && r.diningStation.Map == map
                && !r.diningStation.IsBurning() && r.pawn.CurJob?.loadID == r.mealJobId;
        }
        public void EndDining(WorkMealSession r)
        {
            Thing station = r.diningStation;
            Job meal = r.pawn?.CurJob;
            if (station != null && meal?.loadID == r.mealJobId && map.reservationManager.ReservedBy(station, r.pawn, meal))
                map.reservationManager.Release(station, r.pawn, meal);
            r.diningStation = null;
            r.diningCell = IntVec3.Invalid;
            if (r.diningOnly) breaks.Remove(r);
        }
        public Job WorkJob(WorkMealSession record)
        {
            if (record.pawn?.CurJob?.loadID == record.workJobId) return record.pawn.CurJob;
            return record.pawn?.jobs?.jobQueue.FirstOrDefault(q => q?.job?.loadID == record.workJobId)?.job;
        }
        public bool Valid(WorkMealSession r)
        {
            Pawn p = r.pawn;
            if (!(r.diningOnly ? DeskLunchMod.Settings.idleWorktables : r.atWork ? DeskLunchMod.Settings.enabled : DeskLunchMod.Settings.lunchBreaks) || p == null || !p.Spawned || p.Map != map || p.Dead || p.Downed
                || p.Drafted || p.InMentalState || !MealWorker.Supported(p) || Find.TickManager.TicksGame >= r.deadline) return false;
            if (r.diningOnly) return DiningValid(r);
            if (!r.atWork && !MealSchedule.AllowsWorkMeals(p)) return false;
            Job work = WorkJob(r);
            return work != null && r.station != null && r.station.Spawned && r.station.Map == map
                && !r.station.IsForbidden(p) && !r.station.IsBurning()
                && (work.bill == null ? r.atWork && p.jobs.curDriver is JobDriver_Research : !work.bill.DeletedOrDereferenced && !work.bill.suspended)
                && (work.workGiverDef == null || !p.WorkTypeIsDisabled(work.workGiverDef.workType))
                && (!(r.station is IBillGiver giver) || giver.CurrentlyUsableForBills())
                && r.protectedThings.All(t => t != null && !t.Destroyed && t.Spawned && t.Map == map)
                && r.AcceptsJob(p.CurJob?.loadID);
        }
        public void Cancel(WorkMealSession r)
        {
            if (!breaks.Remove(r)) return;
            if (r.diningStation != null) EndDining(r);
            if (r.diningOnly) return;
            if (r.atWork)
            {
                if (r.pawn?.jobs?.curDriver != null)
                {
                    var state = LunchUtility.State(r.pawn.jobs.curDriver);
                    bool waiting = state.waitingForMeal;
                    state.Reset();
                    state.waitingForMeal = waiting;
                }
                // Active work still needs its vanilla reservations; they are released
                // by its normal job cleanup, not by the end of the meal overlay.
                return;
            }
            Job work = WorkJob(r);
            if (work == null) return;
            // Only remove our suspended job; never clear the player's other orders.
            var queued = r.pawn.jobs.jobQueue.Extract(work);
            if (queued != null) queued.Cleanup(r.pawn, canReturnToPool: false);
            else r.pawn.ClearReservationsForJob(work);
        }
        public override void MapComponentTick()
        {
            if (Find.TickManager.TicksGame % 60 != 0) return;
            foreach (var r in breaks.ToArray())
            {
                if (Valid(r)) continue;
                bool returning = !r.atWork && (r.pawn?.CurJob?.loadID == r.workJobId || r.diningOnly && r.pawn?.CurJob?.loadID == r.mealJobId);
                Cancel(r);
                if (returning) r.pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
            }
        }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref breaks, "mealsAtWorkLunchBreaks", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) breaks ??= new List<WorkMealSession>();
        }
    }

    public static class LunchBreakUtility
    {
        public static WorkMealReservations Manager(Pawn pawn) => pawn?.Map?.GetComponent<WorkMealReservations>();
        public static WorkMealSession Capture(Pawn p, bool atWork, int now)
        {
            var driver = p.jobs.curDriver as JobDriver_DoBill;
            var record = new WorkMealSession
            {
                pawn = p, station = p.CurJob.targetA.Thing, workJobId = p.CurJob.loadID,
                deadline = now + 7500, atWork = atWork,
                workLeft = driver?.workLeft ?? 0f, workTicks = driver?.ticksSpentDoingRecipeWork ?? 0
            };
            if (p.CurJob.targetB.Thing is UnfinishedThing uft) record.protectedThings.Add(uft);
            else if (p.CurJob.placedThings != null)
                foreach (var placed in p.CurJob.placedThings)
                    if (placed.Count > 0 && !record.protectedThings.Contains(placed.thing)) record.protectedThings.Add(placed.thing);
            return record;
        }
        public static void TrackAtWork(Pawn p)
        {
            var manager = Manager(p);
            if (manager != null && manager.Get(p) == null) manager.Add(Capture(p, true, Find.TickManager.TicksGame));
        }
        public static void EndAtWork(Pawn p)
        {
            var manager = Manager(p);
            var record = manager?.Get(p);
            if (record?.atWork == true) manager.Resumed(record);
        }
        public static bool CanStart(Pawn p)
        {
            return DeskLunchMod.Settings.lunchBreaks && p != null && p.Spawned && MealWorker.Supported(p) && MealSchedule.AllowsWorkMeals(p)
                && !p.Drafted && !p.Downed && !p.InMentalState && p.needs?.food != null
                && p.jobs?.curDriver is JobDriver_DoBill driver && (driver.GetType() == typeof(JobDriver_DoBill) || driver is JobDriver_ResumeWork)
                && LunchUtility.IsWorking(p) && !LunchUtility.State(driver).waitingForMeal
                && p.CurJob.targetA.Thing is Building_WorkTable && Manager(p).Get(p) == null;
        }
        public static void Prepare(Pawn p, Job eat)
        {
            var record = Capture(p, false, Find.TickManager.TicksGame);
            record.mealJobId = eat.loadID;
            // Only this job becomes suspendable. DoBill's global definition remains unchanged.
            p.CurJob.def = DefDatabase<JobDef>.GetNamed("MealsAtWork_ResumeWork");
            eat.def = DefDatabase<JobDef>.GetNamed("MealsAtWork_LunchBreak");
            Manager(p).Add(record);
        }
    }

    public sealed class JobDriver_LunchBreak : JobDriver_Ingest
    {
        protected override IEnumerable<Toil> MakeNewToils()
        {
            foreach (var toil in base.MakeNewToils()) yield return toil;
            var finished = ToilMaker.MakeToil("MealsAtWork_MealFinished");
            finished.initAction = () =>
            {
                var record = LunchBreakUtility.Manager(pawn)?.Get(pawn);
                if (record != null) record.ate = true;
            };
            finished.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return finished;
        }
    }

    public sealed class JobDriver_ResumeWork : JobDriver_DoBill
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            var manager = LunchBreakUtility.Manager(pawn);
            var record = manager?.Get(pawn);
            return record != null && record.ate && manager.Valid(record) && base.TryMakePreToilReservations(errorOnFailed);
        }
        protected override IEnumerable<Toil> MakeNewToils()
        {
            // Enumerate vanilla once so its global failure conditions are still installed.
            var original = base.MakeNewToils().ToList();
            int index = original.FindIndex(LunchUtility.IsWorkToil);
            if (index < 0) throw new System.InvalidOperationException("Meals at Work: recipe work toil not found");
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell).FailOnDespawnedNullOrForbidden(TargetIndex.A);
            var work = original[index];
            var initialize = work.initAction;
            work.initAction = () =>
            {
                var manager = LunchBreakUtility.Manager(pawn);
                var record = manager?.Get(pawn);
                if (record == null || !manager.Valid(record)) { EndJobWith(JobCondition.Incompletable); return; }
                initialize();
                record.RestoreProgress(this);
                manager.Resumed(record); // The running job now owns normal reservations again.
            };
            for (int i = index; i < original.Count; i++) yield return original[i];
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.StartJob))]
    static class LunchBreakStartPatch
    {
        static void Prefix(Pawn ___pawn, Job newJob, ref bool resumeCurJobAfterwards)
        {
            var manager = LunchBreakUtility.Manager(___pawn);
            var record = manager?.Get(___pawn);
            if (record != null && manager.WorkJob(record) != null) record.AcceptCompletionTransition(newJob);
            if (record != null && !record.AcceptsJob(newJob?.loadID)) manager.Cancel(record);
            if (newJob?.def == JobDefOf.Ingest && !newJob.playerForced && LunchBreakUtility.CanStart(___pawn))
            {
                LunchBreakUtility.Prepare(___pawn, newJob);
                resumeCurJobAfterwards = true;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    static class LunchBreakEndPatch
    {
        static void Prefix(Pawn ___pawn, JobCondition condition, bool startNewJob)
        {
            var manager = LunchBreakUtility.Manager(___pawn);
            var record = manager?.Get(___pawn);
            if (record != null && (condition != JobCondition.Succeeded || !startNewJob || !record.ate)) manager.Cancel(record);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.DeSpawn))]
    static class LunchBreakDespawnPatch
    {
        static void Prefix(Pawn __instance)
        {
            var manager = LunchBreakUtility.Manager(__instance);
            var record = manager?.Get(__instance);
            if (record != null) manager.Cancel(record);
        }
    }

    [HarmonyPatch(typeof(HaulAIUtility), nameof(HaulAIUtility.PawnCanAutomaticallyHaulFast))]
    static class LunchBreakHaulPatch
    {
        static bool Prefix(Thing t, bool forced, ref bool __result)
        {
            var manager = t?.Map?.GetComponent<WorkMealReservations>();
            var record = manager?.Protecting(t);
            if (record == null) return true;
            if (forced) { manager.Cancel(record); return true; }
            __result = false;
            return false;
        }
    }

}

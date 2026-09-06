using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Xml.Linq;
using DeskLunch;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

static class Program
{
    static int checks;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }
    static void Main()
    {
        try { Run(); }
        catch (Exception error)
        {
            Console.WriteLine(error.GetType().FullName);
            Console.WriteLine(error.Message);
            Console.WriteLine(error.StackTrace);
            Environment.ExitCode = 1;
        }
    }
    static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../.."));
        foreach (string file in Directory.GetFiles(root, "*.xml", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
            XDocument.Load(file);
        var thought = XDocument.Load(Path.Combine(root, "Defs/ThoughtDefs.xml")).Root.Element("ThoughtDef");
        Check(Math.Abs((double)thought.Element("durationDays") * 60000 - 2500) < 0.01, "Mood penalty must last exactly one game hour");
        Check((int)thought.Element("stackLimit") == 1, "Mood penalty stacks");
        Check((int)thought.Element("stages").Element("li").Element("baseMoodEffect") == -1, "Incorrect mood penalty");
        var english = XDocument.Load(Path.Combine(root, "Languages/English/Keyed/DeskLunch.xml")).Root.Elements().Select(e => e.Name.LocalName).OrderBy(n => n);
        var chinese = XDocument.Load(Path.Combine(root, "Languages/ChineseSimplified/Keyed/DeskLunch.xml")).Root.Elements().Select(e => e.Name.LocalName).OrderBy(n => n);
        Check(english.SequenceEqual(chinese), "Missing UI translation");
        // All batch sizes must give the same total work as single ticking, including the meal boundary.
        for (int duration = 1; duration <= 1000; duration++)
        {
            foreach (int batch in new[] { 1, 3, 10, 60, 150, 250 })
            {
                int elapsed = 0, work = 0, remainder = 0, total = duration + 300;
                while (elapsed < total)
                {
                    int delta = Math.Min(batch, total - elapsed);
                    int effective = LunchTiming.WorkTicks(delta, duration - elapsed, ref remainder);
                    Check(effective >= 0 && effective <= delta, "Invalid per-interval work count");
                    work += effective;
                    elapsed += delta;
                }
                Check(work == duration * 4 / 5 + 300, "Batch-dependent work speed");
            }
        }
        var state = new LunchState { duration = 500, remaining = 250, remainder = 3, toilIndex = 1, waitingForMeal = true };
        state.Reset();
        Check(state.duration == 0 && state.remaining == 0 && state.remainder == 0 && state.toilIndex == -1 && !state.waitingForMeal, "Interrupted lunch retained progress");

        var harmony = new Harmony("ch4acko3.desklunch.tests");
        harmony.PatchAll(typeof(DeskLunchMod).Assembly);
        Check(Harmony.GetAllPatchedMethods().Count(m => Harmony.GetPatchInfo(m).Owners.Contains(harmony.Id)) == 20, "Missing Harmony patches");
        var toil = new Toil { tickIntervalAction = _ => { } };
        var before = toil.tickIntervalAction;
        LunchUtility.EnableFor(toil);
        Check(before != toil.tickIntervalAction, "Work toil not wrapped");
        var once = toil.tickIntervalAction;
        LunchUtility.EnableFor(toil);
        Check(once == toil.tickIntervalAction, "Work toil double wrapped");
        toil.Clear();
        toil.tickIntervalAction = before;
        LunchUtility.EnableFor(toil);
        Check(before != toil.tickIntervalAction, "Pooled toil retained old registration");
        TestCompletionHold(harmony);
        TestSharedMealReservations(harmony);
        TestIdleTableAdmission(harmony);
        TestDeliveryTiming();
        TestMealSchedule();
        TestCompletionTransition();
        harmony.UnpatchAll(harmony.Id);
        Console.WriteLine($"PASS: {checks} assertions; XML/settings, timing, shared reservations, new-bill dining continuity, hunger orders/fixed deadline/courier retry, 20 Harmony targets.");
    }

    static void TestDeliveryTiming()
    {
        Check(IdleWorktableDining.PreferNormalTable(36, 9, 2), "Equal weighted distance should favor table");
        Check(IdleWorktableDining.PreferNormalTable(25, 9, 2), "Nearby formal table lost preference");
        Check(!IdleWorktableDining.PreferNormalTable(49, 9, 2), "Distant formal table still wins unconditionally");
        Check(!IdleWorktableDining.PreferNormalTable(16, 9, 1), "Weight one must use actual distance");
        var o = new MealOrder { created = 100, deliveryJob = 12 };
        Check(o.WaitWithinLimit(100, 2500), "Unclaimed order must allow initial wait");
        Check(o.WaitWithinLimit(2599, 2500), "Order expired early");
        Check(!o.WaitWithinLimit(2600, 2500), "Order exceeded fixed deadline");
        o.lastWork = 2500; o.waitStarted = 2500; o.pickedUp = true;
        Check(!o.WaitWithinLimit(2600, 2500), "Work/pickup reset deadline");
        Check(!o.WaitWithinLimit(100, 0), "Disabled wait still defers food");
        var retries = new MealDelivery(null);
        o.courier = new Pawn(); retries.orders.Add(o);
        retries.ReleaseCourier(o);
        Check(retries.orders.Contains(o) && o.courier == null && !o.pickedUp && o.deliveryJob == -1 && o.created == 100,
            "Courier interruption deleted or renewed order");
        var bill = new Job { def = new JobDef { driverClass = typeof(JobDriver_DoBill) }, targetA = new Building_WorkTable() };
        Check(MealDelivery.WorkJob(bill), "Work stages not admitted");
        Check(!MealDelivery.WorkJob(new Job { def = new JobDef { driverClass = typeof(JobDriver_Ingest) }, targetA = new Building_WorkTable() }), "Non-work admitted");
        var manager = new MealDelivery(null);
        var courierA = new Pawn { thingIDNumber = 801 };
        var courierB = new Pawn { thingIDNumber = 802 };
        manager.orders.Add(o);
        Check(manager.Claim(o, courierA, 1), "First courier cannot claim order");
        Check(!manager.Claim(o, courierB, 2) && o.courier == courierA && o.deliveryJob == 1, "Two couriers claimed one order");
        manager.orders.Remove(o);
        o.courier = null;
        Check(!manager.Claim(o, courierB, 2), "Courier claimed cancelled order");
    }

    static void TestMealSchedule()
    {
        foreach (string name in new[] { "Mazo_Cook", "Mazo_Smith", "Mazo_Tailor", "Mazo_Art", "Mazo_Craft", "Mazo_Research" })
            Check(MealSchedule.AllowsWorkMeals(new TimeAssignmentDef { defName = name }), "Schedule Everything work rejected: " + name);
        foreach (string name in new[] { "Mazo_Patient", "Mazo_Bedrest", "Mazo_Haul", "Mazo_Unknown" })
            Check(!MealSchedule.AllowsWorkMeals(new TimeAssignmentDef { defName = name }), "Non-workstation schedule admitted: " + name);
        Check(MealSchedule.AllowsWorkMeals(new TimeAssignmentDef { defName = "CustomWork", modExtensions = new List<DefModExtension> { new WorkMealScheduleExtension { allowWorkMeals = true } } }), "Schedule extension ignored");
        Check(!MealCargo.IsCargo(new Pawn(), new ThingWithComps()), "Absent optional mods mark personal food as cargo");
        foreach (string name in new[] { "Anything", "Work", "Joy", "Sleep", "Meditate", "CustomMealTime" })
            Check(MealSchedule.AllowsWorkMeals(new TimeAssignmentDef { defName = name }) == (name == "Anything" || name == "Work"),
                "Incorrect work meal schedule: " + name);
        Check(MealSchedule.AllowsWorkMeals((TimeAssignmentDef)null), "Missing timetable should use Anything");
        var settings = new DeskLunchSettings();
        Check(settings.deliveryWait == 5000, "Default wait must be two game hours");
        var order = new MealOrder { created = 100 };
        Check(order.WaitWithinLimit(100, settings.deliveryWait), "Two-hour wait did not start");
        Check(order.WaitWithinLimit(5099, settings.deliveryWait), "Two-hour wait expired early");
        Check(!order.WaitWithinLimit(5100, settings.deliveryWait), "Two-hour wait exceeded deadline");
        foreach (int old in new[] { 0, 500, 1250, 2000, 2500 })
        {
            settings.deliveryWait = old;
            AccessTools.Field(typeof(DeskLunchSettings), "deliverySettingsVersion").SetValue(settings, 0);
            settings.UpgradeDeliverySettings();
            Check(settings.deliveryWait == (old == 1250 || old == 2500 ? 5000 : old), "Old wait setting migration failed");
        }
        settings.deliveryWait = 1250;
        settings.UpgradeDeliverySettings();
        Check(settings.deliveryWait == 1250, "Migration overwrites a new custom setting");
    }

    static void TestCompletionTransition()
    {
        var postureDef = new JobDef { defName = "Wait_MaintainPosture" };
        {
            var record = new WorkMealSession { workJobId = 36, mealJobId = 77, ate = true };
            var transition = new Job { def = postureDef, loadID = 78, expiryInterval = 1 };
            Check(record.AcceptCompletionTransition(transition) && record.AcceptsJob(78), "Vanilla completion posture tick cancels lunch return");
            Check(record.AcceptsJob(36) && !record.AcceptsJob(79), "Transition admission accepts unrelated job");
            transition.loadID = 79;
            Check(!record.AcceptCompletionTransition(transition), "Repeated transition extends session");
            Check(!new WorkMealSession { ate = false }.AcceptCompletionTransition(transition), "Pre-meal transition allowed");
            Check(!new WorkMealSession { ate = true, atWork = true }.AcceptCompletionTransition(transition), "Desk meal allows transition");
            Check(!new WorkMealSession { ate = true, diningOnly = true }.AcceptCompletionTransition(transition), "Idle dining allows transition");
            transition.expiryInterval = 60;
            Check(!new WorkMealSession { ate = true }.AcceptCompletionTransition(transition), "Long wait mistaken for completion tick");
        }
    }

    static bool SpawnedForTest(ref bool __result) { __result = true; return false; }
    static bool NoMapForTest(ref Map __result) { __result = null; return false; }
    static bool NotBurningForTest(ref bool __result) { __result = false; return false; }
    static void TestIdleTableAdmission(Harmony harmony)
    {
        // Mock only scene presence/fire. BillStack, session validation and job identity
        // below are the production implementations, including changes mid-meal.
        harmony.Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Spawned)), prefix: new HarmonyMethod(typeof(Program), nameof(SpawnedForTest)));
        harmony.Patch(AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Map)), prefix: new HarmonyMethod(typeof(Program), nameof(NoMapForTest)));
        harmony.Patch(AccessTools.Method(typeof(FireUtility), nameof(FireUtility.IsBurning), new[] { typeof(Thing) }), prefix: new HarmonyMethod(typeof(Program), nameof(NotBurningForTest)));
        var table = new Building_WorkTable();
        var pawn = new Pawn();
        var meal = new Job { loadID = 401 };
        pawn.jobs = new Pawn_JobTracker(pawn) { curJob = meal };
        var session = new WorkMealSession { pawn = pawn, diningOnly = true, diningStation = table, mealJobId = 401, workJobId = -1 };
        var manager = new WorkMealReservations(null);
        manager.Add(session);
        Check(IdleWorktableDining.EmptyForDining(table) && manager.DiningValid(session), "Empty table rejected");
        var bill = (Bill_Production)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Bill_Production));
        table.BillStack.AddBill(bill);
        Check(!IdleWorktableDining.EmptyForDining(table), "New diners allowed at a table with bills");
        Check(manager.DiningValid(session) && manager.Get(pawn) == session, "New bill evicts existing diner");
        bill.suspended = true;
        Check(!IdleWorktableDining.EmptyForDining(table) && manager.DiningValid(session), "Suspended bill changes admission/continuity rules");
        pawn.jobs.curJob = new Job { loadID = 402 };
        Check(!manager.DiningValid(session) && !session.AcceptsJob(402), "Reassignment retained dining session");
        pawn.jobs.curJob = meal;
        session.diningStation = null;
        Check(!manager.DiningValid(session), "Missing table retained dining session");
    }

    static readonly List<Job> released = new List<Job>();
    static bool CaptureRelease(Job job) { released.Add(job); return false; }
    static void TestSharedMealReservations(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.ClearReservationsForJob)),
            prefix: new HarmonyMethod(typeof(Program), nameof(CaptureRelease)));
        var pawn = new Pawn();
        var itemDef = (ThingDef)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ThingDef));
        itemDef.category = ThingCategory.Item;
        var unfinished = new UnfinishedThing { def = itemDef, thingIDNumber = 501 };
        var ingredient = new Thing { stackCount = 10, def = itemDef, thingIDNumber = 502 };
        var work = new Job { loadID = 201, targetA = new Building_WorkTable(), targetB = unfinished,
            placedThings = new List<ThingCountClass> { new ThingCountClass(ingredient, 5) } };
        var driver = new JobDriver_DoBill { pawn = pawn, job = work, workLeft = 137.5f, ticksSpentDoingRecipeWork = 321 };
        pawn.jobs = new Pawn_JobTracker(pawn) { curJob = work, curDriver = driver };
        var desk = LunchBreakUtility.Capture(pawn, true, 100);
        var outside = LunchBreakUtility.Capture(pawn, false, 100);
        outside.mealJobId = 202;
        Check(desk.protectedThings.SequenceEqual(outside.protectedThings) && desk.protectedThings.Single() == unfinished,
            "Both modes must protect the same UFT, not its now-unspawned ingredients");
        Check(desk.deadline == 7600 && outside.deadline == desk.deadline, "Modes use different timeout limits");
        Check(desk.AcceptsJob(201) && !desk.AcceptsJob(202) && !desk.AcceptsJob(null), "At-work reassignment not detected");
        Check(outside.AcceptsJob(202) && !outside.AcceptsJob(201), "Returning allowed before eating");
        outside.ate = true;
        Check(outside.AcceptsJob(201) && !outside.AcceptsJob(999), "Outside return/reassignment gate incorrect");
        driver.workLeft = 999;
        driver.ticksSpentDoingRecipeWork = 0;
        outside.RestoreProgress(driver);
        Check(driver.workLeft == 137.5f && unfinished.workLeft == 137.5f && driver.ticksSpentDoingRecipeWork == 321,
            "Returning lost progress or XP accounting");
        work.targetB = ingredient;
        var noUft = LunchBreakUtility.Capture(pawn, false, 100);
        Check(noUft.protectedThings.Single() == ingredient, "Non-UFT recipe did not protect staged materials");

        var manager = new WorkMealReservations(null);
        var unrelated = new Job { loadID = 999 };
        pawn.jobs.jobQueue.EnqueueLast(unrelated);
        manager.Add(desk);
        var meal = LunchUtility.State(driver);
        meal.meal = new Thing();
        meal.waitingForMeal = true;
        manager.Cancel(desk);
        Check(manager.Get(pawn) == null && released.Count == 0 && pawn.CurJob == work && pawn.jobs.jobQueue.Contains(unrelated),
            "Desk meal cancellation released active work or unrelated orders");
        Check(meal.meal == null && meal.waitingForMeal, "Cancelling held meal repeats completed production");
        pawn.jobs.curJob = new Job { loadID = 202 };
        pawn.jobs.jobQueue.EnqueueFirst(work);
        manager.Add(outside);
        manager.Cancel(outside);
        Check(!pawn.jobs.jobQueue.Contains(work) && pawn.jobs.jobQueue.Contains(unrelated) && released.Single() == work,
            "Outside cancellation must release only the suspended work reservation");
        manager.Cancel(outside);
        Check(released.Count == 1 && !unfinished.Destroyed, "Cancellation is not idempotent or destroyed UFT");
    }

    static int resumes;
    // Isolate map/food simulation only; exercise the real wrapped toil and completion
    // patch against an actual JobDriver, including repeated native completion requests.
    static bool Eligible(ref bool __result) { __result = true; return false; }
    static bool SimulateChewing(Pawn pawn, int delta, bool allowStart, ref int __result)
    {
        var state = LunchUtility.State(pawn.jobs.curDriver);
        Check(!state.waitingForMeal || !allowStart, "Waiting started a second meal");
        state.remaining -= delta;
        if (state.remaining <= 0) state.Reset();
        __result = delta;
        return false;
    }
    static bool ObserveResume(JobDriver __instance)
    {
        if (LunchUtility.HasActiveMeal(__instance)) return true;
        resumes++;
        return false; // No real map in this harness; don't run vanilla next-toil initialization.
    }
    static void TestCompletionHold(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(LunchUtility), "AtWork"), prefix: new HarmonyMethod(typeof(Program), nameof(Eligible)));
        harmony.Patch(AccessTools.Method(typeof(LunchUtility), "Suitable"), prefix: new HarmonyMethod(typeof(Program), nameof(Eligible)));
        harmony.Patch(AccessTools.Method(typeof(LunchUtility), "Tick"), prefix: new HarmonyMethod(typeof(Program), nameof(SimulateChewing)));
        harmony.Patch(AccessTools.Method(typeof(JobDriver), "ReadyForNextToil"), prefix: new HarmonyMethod(typeof(Program), nameof(ObserveResume)));
        var pawn = new Pawn();
        var job = new Job();
        var driver = new JobDriver_DoBill { pawn = pawn, job = job };
        pawn.jobs = new Pawn_JobTracker(pawn) { curJob = job, curDriver = driver };
        var meal = new Thing();
        var state = LunchUtility.State(driver);
        state.meal = meal;
        state.duration = state.remaining = 10;
        state.toilIndex = 0;
        int workCalls = 0, machineryCalls = 0;
        var toil = new Toil { actor = pawn, tickAction = () => machineryCalls++, tickIntervalAction = _ => { workCalls++; driver.ReadyForNextToil(); } };
        AccessTools.Field(typeof(JobDriver), "toils").SetValue(driver, new List<Toil> { toil });
        AccessTools.Field(typeof(JobDriver), "curToilIndex").SetValue(driver, 0);
        LunchUtility.EnableFor(toil);
        toil.tickIntervalAction(1);
        Check(state.waitingForMeal && workCalls == 1 && resumes == 0, "Work did not hold for unfinished meal");
        driver.ReadyForNextToil();
        toil.tickAction();
        toil.tickIntervalAction(5);
        Check(state.waitingForMeal && state.remaining == 4 && workCalls == 1 && machineryCalls == 0 && resumes == 0,
            "Waiting repeated production or released job early");
        Check(ReferenceEquals(pawn.CurJob, job), "Waiting replaced job (and its reservations)");
        toil.tickIntervalAction(4);
        Check(resumes == 1 && workCalls == 1 && state.meal == null, "Meal completion did not resume exactly once");
    }
}



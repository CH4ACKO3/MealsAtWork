using System;
using System.IO;
using System.Linq;
using System.Text;
using DeskLunch;
using RimWorld;
using Verse;
using Verse.AI;
using LudeonTK;
using HarmonyLib;

namespace MealsLive
{
    [StaticConstructorOnStartup]
    public static class Diagnostics
    {
        static Diagnostics() { new Harmony("meals.live.diagnostics").Patch(AccessTools.Method(typeof(WorkMealReservations), "Cancel"), prefix: new HarmonyMethod(typeof(Diagnostics), nameof(Cancelling))); }
        static void Cancelling(WorkMealReservations __instance, WorkMealSession r)
        {
            if (r?.pawn?.Name.ToStringShort != "Worker") return;
            File.AppendAllText(Probe.Folder + "cancels.txt", "tick=" + Find.TickManager.TicksGame + " atWork=" + r.atWork + " ate=" + r.ate
                + " cur=" + r.pawn.CurJob?.def.defName + "#" + r.pawn.CurJob?.loadID + " work=" + __instance.WorkJob(r)?.loadID
                + " valid=" + __instance.Valid(r) + "\n" + Environment.StackTrace + "\n");
        }
    }
    public static class Probe
    {
        public const string Folder = "D:/Projects/rimworld/work/meals-live-20260906/";
        public static Pawn Worker => Find.CurrentMap.mapPawns.FreeColonists.Last(p => p.Name.ToStringShort == "Worker");
        public static Pawn Courier => Find.CurrentMap.mapPawns.FreeColonists.Last(p => p.Name.ToStringShort == "Courier");
        public static Building_WorkTable Bench => Find.CurrentMap.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>().First(t => t.def.defName == "HandTailoringBench");
        [DebugAction("Meals live", "Run command", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void Command()
        {
            try
            {
                string command = File.ReadAllText(Folder + "command.txt").Trim();
                if (command.StartsWith("batch")) { BatchProbe.Run(command); return; }
                if (command.StartsWith("mech")) { MechProbe.Run(command); return; }
                if (command.StartsWith("draftTest")) { DraftProbe.Change(command); return; }
                if (command == "draftInspect") { DraftProbe.Run(); return; }
                if (command.StartsWith("hunger")) { HungerProbe.Run(command); Snapshot(command); return; }
                if (command.StartsWith("compat")) { CompatProbe.Run(command); Snapshot(command); return; }
                switch (command)
                {
                    case "downloadSchedule": File.AppendAllText(Folder + "events.txt", "downloadSchedule=" + Steamworks.SteamUGC.DownloadItem(new Steamworks.PublishedFileId_t(3486341728UL), true) + "\n"); return;
                    case "setup": Setup(); break;
                    case "hungry": Worker.needs.food.CurLevel = Worker.needs.food.MaxLevel * (Worker.RaceProps.FoodLevelPercentageWantEat - 0.01f); break;
                    case "sleep": Schedule(Worker, TimeAssignmentDefOf.Sleep); break;
                    case "joy": Schedule(Worker, TimeAssignmentDefOf.Joy); break;
                    case "work": Schedule(Worker, TimeAssignmentDefOf.Work); break;
                    case "anything": Schedule(Worker, TimeAssignmentDefOf.Anything); break;
                    case "draftCourier": Courier.drafter.Drafted = true; break;
                    case "undraftCourier": Courier.drafter.Drafted = false; break;
                    case "finishWork": ((JobDriver_DoBill)Worker.jobs.curDriver).workLeft = 1; break;
                    case "stopCourier": Courier.pather.StopDead(); break;
                    case "naturalWork": Worker.CurJob.playerForced = false; break;
                    case "break":
                        Worker.needs.food.CurLevel = 0.29f;
                        Worker.CurJob.playerForced = false;
                        var foodJob = (Job)typeof(JobGiver_GetFood).GetMethod("TryGiveJob", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(new JobGiver_GetFood(), new object[] { Worker });
                        if (foodJob == null) throw new Exception("No food job for lunch break");
                        Worker.jobs.StartJob(foodJob, JobCondition.InterruptOptional);
                        break;
                    case "compete":
                        Pawn second = SpawnPawn("Second", Courier.Position + IntVec3.North);
                        var scanner = new WorkGiver_DeliverMeal();
                        Job proposalA = scanner.JobOnThing(Courier, Worker), proposalB = scanner.JobOnThing(second, Worker);
                        if (proposalA == null || proposalB == null) throw new Exception("Expected two initial delivery proposals");
                        Courier.jobs.StartJob(proposalA, JobCondition.InterruptForced);
                        var otherDriver = new JobDriver_DeliverMeal { pawn = second, job = proposalB };
                        File.AppendAllText(Folder + "events.txt", "secondReservation=" + otherDriver.TryMakePreToilReservations(false) + "\n");
                        second.drafter.Drafted = true;
                        break;
                    case "thoughtChecks": ThoughtChecks(); break;
                    case "socialChecks": SocialChecks(); break;
                    case "waitQuery": File.AppendAllText(Folder + "events.txt", "waitQuery=" + MealDelivery.ShouldWait(Worker) + "\n"); break;
                    case "snapshot": break;
                    default: throw new Exception("Unknown command " + command);
                }
                Snapshot(command);
            }
            catch (Exception e) { File.AppendAllText(Folder + "events.txt", e + "\n"); throw; }
        }
        public static void Schedule(Pawn p, TimeAssignmentDef assignment)
        { for (int h = 0; h < 24; h++) p.timetable.SetAssignment(h, assignment); }
        static void ThoughtChecks()
        {
            Pawn p = Worker;
            var memories = p.needs.mood.thoughts.memories;
            ThoughtDef def = DefDatabase<ThoughtDef>.GetNamed("DeskLunch_AteWhileWorking");
            foreach (Trait t in p.story.traits.allTraits.ToArray()) p.story.traits.RemoveTrait(t);
            memories.RemoveMemoriesOfDef(def);
            Thing meal = ThingMaker.MakeThing(ThingDefOf.MealSimple);
            p.inventory.innerContainer.TryAdd(meal);
            LunchUtility.FinishMeal(p, meal, 0.9f);
            var memory = memories.Memories.FirstOrDefault(m => m.def == def);
            if (memory == null || memory.MoodOffset() != -1) throw new Exception("Ordinary pawn missing -1 workstation thought");
            p.story.traits.GainTrait(new Trait(TraitDefOf.Ascetic));
            if (LunchUtility.ShouldApplyPenalty(p) || memory.MoodOffset() != 0 || !memory.ShouldDiscard)
                throw new Exception("Ascetic did not suppress existing workstation thought");
            memories.RemoveMemoriesOfDef(def);
            meal = ThingMaker.MakeThing(ThingDefOf.MealSimple); p.inventory.innerContainer.TryAdd(meal);
            LunchUtility.FinishMeal(p, meal, 0.9f);
            if (memories.Memories.Any(m => m.def == def)) throw new Exception("Ascetic gained workstation thought");
            File.AppendAllText(Folder + "events.txt", "thoughtChecks=PASS ordinary -1; ascetic existing/new suppressed\n");
        }
        static void SocialChecks()
        {
            Map map = Find.CurrentMap;
            var rect = CellRect.FromLimits(150, 100, 156, 106);
            foreach (IntVec3 cell in rect)
            {
                foreach (Thing t in cell.GetThingList(map).ToArray()) if (!(t is Pawn)) t.Destroy();
                map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil); map.fogGrid.Unfog(cell);
            }
            foreach (IntVec3 cell in rect.EdgeCells)
                GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog), cell, map);
            var bed = (Building_Bed)ThingMaker.MakeThing(ThingDefOf.Bed, ThingDefOf.WoodLog);
            bed.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bed, new IntVec3(152, 0, 102), map); bed.ForPrisoners = true;
            map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
            Thing meal = ThingMaker.MakeThing(ThingDefOf.MealSimple); GenSpawn.Spawn(meal, new IntVec3(154, 0, 103), map);
            bool wasAccepted = MealDelivery.MealFor(Worker, meal);
            bool acceptedNow = MealDelivery.Deliverable(Worker, Courier, meal);
            if (!wasAccepted || acceptedNow || !meal.GetRoom().IsPrisonCell) throw new Exception("Prison meal filter regression");
            File.AppendAllText(Folder + "events.txt", "socialChecks=PASS prison meal old filter true; new filter false\n");
        }
        static void Setup()
        {
            var map = Find.CurrentMap;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            foreach (Pawn p in map.mapPawns.FreeColonists.ToArray())
            {
                p.jobs.StopAll(); p.drafter.Drafted = true;
                p.inventory.innerContainer.ClearAndDestroyContents();
            }
            foreach (Thing t in map.listerThings.AllThings.ToArray())
                if (!(t is Pawn) && t.Position.x >= 95 && t.Position.x <= 145 && t.Position.z >= 95 && t.Position.z <= 125)
                    t.Destroy(DestroyMode.Vanish);
            foreach (IntVec3 cell in CellRect.FromLimits(95, 95, 145, 125))
            { map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil); map.fogGrid.Unfog(cell); map.areaManager.Home[cell] = true; }
            Pawn worker = SpawnPawn("Worker", new IntVec3(105, 0, 104));
            Pawn courier = SpawnPawn("Courier", new IntVec3(140, 0, 105));
            worker.workSettings.SetPriority(DefDatabase<WorkTypeDef>.GetNamed("Tailoring"), 1);
            courier.workSettings.SetPriority(WorkTypeDefOf.Hauling, 1);
            worker.skills.GetSkill(SkillDefOf.Crafting).Level = 10;
            worker.needs.food.CurLevel = worker.needs.food.MaxLevel * (worker.RaceProps.FoodLevelPercentageWantEat + 0.015f);
            var bench = (Building_WorkTable)ThingMaker.MakeThing(ThingDef.Named("HandTailoringBench"), ThingDefOf.WoodLog);
            bench.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(bench, new IntVec3(105, 0, 105), map, Rot4.North);
            var recipe = DefDatabase<RecipeDef>.AllDefs.First(r => r.products.Any(p => p.thingDef.defName == "Apparel_Pants") && r.defName.StartsWith("Make_"));
            var bill = (Bill_Production)recipe.MakeNewBill(); bill.repeatMode = BillRepeatModeDefOf.RepeatCount; bill.repeatCount = 20;
            bench.BillStack.AddBill(bill);
            Spawn("Cloth", 75, new IntVec3(107, 0, 105));
            Spawn("MealSimple", 10, new IntVec3(140, 0, 106));
            DeskLunchMod.Settings = new DeskLunchSettings();
            File.WriteAllText(Folder + "trace.txt", "setup tick=" + Find.TickManager.TicksGame + "\n");
            Job work = ((WorkGiver_DoBill)DefDatabase<WorkGiverDef>.AllDefs.First(d => d.workType == DefDatabase<WorkTypeDef>.GetNamed("Tailoring") && d.giverClass == typeof(WorkGiver_DoBill)).Worker).JobOnThing(worker, bench);
            if (work == null) throw new Exception("No native tailoring job");
            worker.jobs.TryTakeOrderedJob(work, JobTag.MiscWork);
            worker.CurJob.playerForced = false;
        }
        public static Pawn SpawnPawn(string name, IntVec3 cell)
        {
            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                forceGenerateNewPawn: true, canGeneratePawnRelations: false, allowDowned: false));
            p.Name = new NameSingle(name);
            GenSpawn.Spawn(p, cell, Find.CurrentMap);
            foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefs) p.workSettings.SetPriority(w, 0);
            Schedule(p, TimeAssignmentDefOf.Work);
            p.needs.food.CurLevel = p.needs.food.MaxLevel;
            p.needs.rest.CurLevel = 1; p.needs.joy.CurLevel = 1;
            return p;
        }
        static void Spawn(string def, int count, IntVec3 cell)
        { Thing t = ThingMaker.MakeThing(ThingDef.Named(def)); t.stackCount = count; GenSpawn.Spawn(t, cell, Find.CurrentMap); }
        public static void Snapshot(string label)
        {
            var sb = new StringBuilder(label + " tick=" + Find.TickManager.TicksGame);
            foreach (Pawn p in Find.CurrentMap.mapPawns.FreeColonists.Where(p => p.Name.ToStringShort == "Worker" || p.Name.ToStringShort == "Courier"))
            {
                var o = MealDelivery.For(p).Get(p);
                sb.Append(" | " + p.Name.ToStringShort + " " + p.Position + " job=" + p.CurJob?.def.defName + "#" + p.CurJob?.loadID
                    + " toil=" + p.jobs.curDriver?.CurToilIndex + " food=" + p.needs.food.CurLevel.ToString("F4")
                    + " carry=" + p.carryTracker.CarriedThing?.Label + " inv=" + string.Join(",", p.inventory.innerContainer.Select(t => t.Label))
                    + " order=" + (o == null ? "none" : "courier:" + o.courier?.ThingID + ",picked:" + o.pickedUp + ",wait:" + o.waitStarted + ",last:" + o.lastWork)
                    + " lunch=" + (p.jobs.curDriver == null ? 0 : LunchUtility.State(p.jobs.curDriver).remaining)
                    + " workLeft=" + (p.jobs.curDriver as JobDriver_DoBill)?.workLeft);
                var session = LunchBreakUtility.Manager(p)?.Get(p);
                sb.Append(" session=" + (session == null ? "none" : "ate:" + session.ate + ",atWork:" + session.atWork + ",valid:" + LunchBreakUtility.Manager(p).Valid(session))
                    + " queue=" + string.Join(",", p.jobs.jobQueue.Select(q => q.job.def.defName + "#" + q.job.loadID)));
            }
            var m = Find.CurrentMap;
            int meals = m.listerThings.AllThings.Where(t => t.def.defName == "MealSimple").Sum(t => t.stackCount)
                + m.mapPawns.AllPawnsSpawned.Sum(p => (p.inventory?.innerContainer.Where(t => t.def.defName == "MealSimple").Sum(t => t.stackCount) ?? 0)
                    + (p.carryTracker?.CarriedThing?.def.defName == "MealSimple" ? p.carryTracker.CarriedThing.stackCount : 0));
            sb.Append(" | meals=" + meals + " pants=" + m.listerThings.AllThings.Count(t => t.def.defName == "Apparel_Pants")
                + " billLeft=" + (Bench.BillStack.Bills.FirstOrDefault() as Bill_Production)?.repeatCount
                + " reserved=" + m.reservationManager.ReservationsReadOnly.Count(r => r.Target.Thing == Bench));
            File.AppendAllText(Folder + "trace.txt", sb + "\n");
        }
    }
    public sealed class Trace : MapComponent
    {
        public Trace(Map map) : base(map) { }
        public override void MapComponentTick()
        {
            if (Find.CurrentMap != map || Find.TickManager.TicksGame % 30 != 0) return;
            if (map.mapPawns.FreeColonists.Any(p => p.Name.ToStringShort == "Worker")) Probe.Snapshot("tick");
        }
    }
}

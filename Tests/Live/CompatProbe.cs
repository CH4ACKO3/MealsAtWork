using System;
using System.IO;
using System.Linq;
using HarmonyLib;
using DeskLunch;
using RimWorld;
using Verse;
using Verse.AI;

namespace MealsLive
{
    public static class CompatProbe
    {
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        static void Passed(string text) => File.AppendAllText(Probe.Folder + "compat-results.txt", text + "\n");
        public static void Run(string command)
        {
            Pawn p = Probe.Worker;
            if (command == "compatFormalTable")
            {
                p.drafter.Drafted = true; p.Position = new IntVec3(103, 0, 102); p.Notify_Teleported();
                Probe.Bench.BillStack.Clear();
                var table = (Building)ThingMaker.MakeThing(ThingDefOf.Table2x2c, ThingDefOf.WoodLog);
                table.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(table, new IntVec3(114, 0, 104), p.Map);
                var chair = (Building)ThingMaker.MakeThing(ThingDefOf.DiningChair, ThingDefOf.WoodLog);
                chair.SetFaction(Faction.OfPlayer); GenSpawn.Spawn(chair, new IntVec3(113, 0, 104), p.Map);
                var eater = Probe.Courier; eater.jobs.StopAll(); eater.Position = new IntVec3(108, 0, 104); eater.Notify_Teleported();
                var meal = ThingMaker.MakeThing(ThingDefOf.MealSimple); eater.inventory.innerContainer.TryAdd(meal);
                var job = JobMaker.MakeJob(JobDefOf.Ingest, meal); job.count = 1;
                eater.jobs.StartJob(job, JobCondition.InterruptForced);
                Assert(Toils_Ingest.TryFindChairOrSpot(eater, meal, out var spot) && spot == chair.Position, "Fixture formal dining spot unavailable");
                Assert((Probe.Bench.InteractionCell-eater.Position).LengthHorizontalSquared < (spot-eater.Position).LengthHorizontalSquared, "Fixture worktable must be closer");
                Assert(!IdleWorktableDining.TryStart(eater,meal) && LunchBreakUtility.Manager(eater).Get(eater)==null, "Closer worktable stole formal table");
                chair.DeSpawn(); table.DeSpawn(); eater.ClearReservationsForJob(job);
                Assert(IdleWorktableDining.TryStart(eater,meal), "Absent formal table prevents worktable fallback");
                Passed("formal table outranks nearer worktable; absent formal table permits fallback PASS");
                return;
            }
            if (command == "compatCargo")
            {
                p.inventory.innerContainer.ClearAndDestroyContents();
                Thing cargo = ThingMaker.MakeThing(ThingDefOf.MealSimple); cargo.stackCount = 5;
                p.inventory.innerContainer.TryAdd(cargo);
                var comp = p.AllComps.First(c => c.GetType().FullName == "PickUpAndHaul.CompHauledToInventory");
                AccessTools.Method(comp.GetType(), "RegisterHauledItem").Invoke(comp, new object[] { cargo });
                Assert(MealCargo.IsCargo(p, cargo) && !MealDelivery.HasMeal(p), "PUAH cargo mistaken for personal meal");
                var set = (System.Collections.Generic.ICollection<Thing>)AccessTools.Field(comp.GetType(), "takenToInventory").GetValue(comp);
                set.Clear();
                var checker = AccessTools.Method("CommonSense.CompUnloadChecker:GetChecker").Invoke(null, new object[] { cargo, true, true });
                Assert(MealCargo.IsCargo(p, cargo) && !MealDelivery.HasMeal(p), "CS unload item mistaken for personal meal");
                AccessTools.Field(checker.GetType(), "ShouldUnload").SetValue(checker, false);
                Assert(!MealCargo.IsCargo(p, cargo) && MealDelivery.HasMeal(p), "Cleared cargo flag still excludes meal");
                set.Add(cargo);
                Thing delivered = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                Probe.Courier.carryTracker.innerContainer.TryAdd(delivered);
                Assert(MealCargo.TransferDelivery(Probe.Courier, p, delivered), "Delivery handoff failed");
                Assert(p.inventory.innerContainer.Count == 2 && cargo.stackCount == 5 && !MealCargo.IsCargo(p, delivered), "Delivery merged into cargo");
                Assert(MealDelivery.HasMeal(p), "Delivered meal unavailable");
                p.needs.food.CurLevel = 0.29f; p.CurJob.playerForced = true;
                Passed("cargo selection + separate handoff PASS; now chew using real work toil");
            }
            else if (command == "compatCargoAssert")
            {
                Assert(p.inventory.innerContainer.Sum(t => t.stackCount) == 5 && p.inventory.innerContainer.All(t => MealCargo.IsCargo(p, t)), "Work meal consumed cargo or failed to finish delivered meal");
                Assert(p.needs.food.CurLevel > 0.8f, "Delivered meal did not replenish nutrition");
                Passed("real work eating + save/load PASS: 1 delivered meal consumed, 5 tagged cargo meals preserved");
            }
            else if (command == "compatSchedule")
            {
                foreach (string name in new[] { "Mazo_Cook", "Mazo_Smith", "Mazo_Tailor", "Mazo_Art", "Mazo_Craft", "Mazo_Research" })
                {
                    var assignment = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(name);
                    Assert(assignment != null && MealSchedule.AllowsWorkMeals(assignment), "Custom work assignment missing or rejected " + name);
                }
                Assert(!MealSchedule.AllowsWorkMeals(DefDatabase<TimeAssignmentDef>.GetNamed("Mazo_Bedrest")), "Bed rest incorrectly allows work meals");
                Probe.Schedule(p, DefDatabase<TimeAssignmentDef>.GetNamed("Mazo_Tailor"));
                p.needs.food.CurLevel = 0.29f;
                var manager = MealDelivery.For(p); manager.orders.Clear(); manager.Observe(p);
                Assert(manager.Get(p) != null, "No order during Schedule Everything tailoring");
                Probe.Schedule(p, DefDatabase<TimeAssignmentDef>.GetNamed("Mazo_Bedrest"));
                Assert(manager.Valid(manager.Get(p)) && !MealDelivery.ShouldWait(p), "Custom bed-rest assignment lost order or blocked food");
                Probe.Schedule(p, DefDatabase<TimeAssignmentDef>.GetNamed("Mazo_Tailor"));
                Passed("Schedule Everything live defs + order admission/cancellation PASS");
            }
            else if (command == "compatIdle")
            {
                p.drafter.Drafted = true;
                p.Position = new IntVec3(103, 0, 102); p.Notify_Teleported();
                Probe.Bench.BillStack.Clear();
                Pawn eater = Probe.Courier;
                eater.jobs.StopAll(); eater.Position = new IntVec3(108, 0, 104); eater.Notify_Teleported();
                eater.needs.food.CurLevel = 0.29f;
                var settings = AccessTools.TypeByName("CommonSense.Settings");
                AccessTools.Field(settings, "adv_cleaning_ingest").SetValue(null, true);
                Thing meal = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                eater.inventory.innerContainer.TryAdd(meal);
                Job job = JobMaker.MakeJob(JobDefOf.Ingest, meal); job.count = 1;
                eater.jobs.StartJob(job, JobCondition.InterruptForced);
                Passed("CS advanced ingest cleaning + empty worktable test started");
            }
            else if (command == "compatIdleAssert")
            {
                Pawn eater = Probe.Courier;
                var record = LunchBreakUtility.Manager(eater).Get(eater);
                Assert(record?.diningStation == Probe.Bench && record.diningCell == Probe.Bench.InteractionCell, "CS advanced ingest did not select empty bench");
                var reservations = eater.Map.reservationManager.ReservationsReadOnly.Where(r => r.Claimant == eater && r.Job == eater.CurJob).ToArray();
                Assert(reservations.Count(r => !r.Target.HasThing || r.Target.Thing is Building) == 2, "Multiple seats reserved for same ingest job");
                var recipe = DefDatabase<RecipeDef>.AllDefs.First(r => r.products.Any(x => x.thingDef.defName == "Apparel_Pants") && r.defName.StartsWith("Make_"));
                Probe.Bench.BillStack.AddBill(recipe.MakeNewBill());
                Assert(LunchBreakUtility.Manager(eater).DiningValid(record), "New bill evicted diner");
                Passed("CS reserved exactly 1 bench + 1 seat; new bill does not evict PASS");
            }
            else if (command == "compatIdleDone")
            {
                Pawn eater = Probe.Courier;
                Assert(LunchBreakUtility.Manager(eater).Get(eater) == null && !eater.Map.reservationManager.ReservationsReadOnly.Any(r => r.Claimant == eater && r.Target.Thing == Probe.Bench), "CS idle dining leaked table reservation");
                Assert(eater.needs.mood.thoughts.memories.Memories.Any(m => m.def.defName == "DeskLunch_AteWhileWorking"), "CS idle dining missed workstation mood");
                Passed("CS idle dining completion/load + reservation cleanup PASS");
            }
            else throw new Exception("Unknown compatibility command");
        }
    }
}

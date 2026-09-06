using System;
using System.IO;
using System.Linq;
using DeskLunch;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace MealsLive
{
    public static class HungerProbe
    {
        static void Check(bool value, string text) { if (!value) throw new Exception(text); }
        public static void Run(string command)
        {
            Pawn p = Probe.Worker;
            var manager = MealDelivery.For(p);
            if (command == "hungerNext")
            {
                Probe.Courier.drafter.Drafted = true;
                p.drafter.Drafted = false; p.jobs.StopAll();
                p.inventory.innerContainer.ClearAndDestroyContents();
                p.Position = new IntVec3(115, 0, 104); p.Notify_Teleported();
                Probe.Schedule(p, TimeAssignmentDefOf.Anything);
                manager.orders.Clear(); manager.timedOut.Clear();
                p.needs.food.CurLevelPercentage = 0.29f;
                object[] args = { null, false };
                var result = (ThinkResult)AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob").Invoke(p.jobs, args);
                Check(MealDelivery.WorkJob(result.Job), "Hungry next job was " + result.Job);
                Check(manager.Get(p) != null, "Next-work selection did not order");
                p.jobs.StartJob(result.Job, JobCondition.InterruptForced, result.SourceNode, thinkTree: (ThinkTreeDef)args[0], tag: result.Tag);
                Check(!LunchUtility.IsWorking(p) && MealDelivery.ShouldWait(p), "Travel/preparation does not defer food");
            }
            else if (command == "hungerStages")
            {
                manager.orders.Clear(); manager.timedOut.Clear();
                p.needs.food.CurLevelPercentage = 0.8f;
                manager.Observe(p); Check(manager.Get(p) == null, "Still preordering");
                p.needs.food.CurLevelPercentage = 0.29f;
                Check(!LunchUtility.IsWorking(p), "Fixture already reached work toil");
                manager.Observe(p);
                Check(manager.Get(p) != null && MealDelivery.ShouldWait(p), "No order in preparation stage");
            }
            else if (command == "hungerDepart")
            {
                var order = manager.Get(p); int created = order.created;
                p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Goto, new IntVec3(118, 0, 105)), JobCondition.InterruptForced);
                Probe.Schedule(p, TimeAssignmentDefOf.Joy);
                Check(manager.Valid(order) && !MealDelivery.ShouldWait(p), "Departure or joy cancelled order / blocked normal needs");
                Probe.Schedule(p, TimeAssignmentDefOf.Work);
                Check(MealDelivery.ShouldWait(p), "Pending order not deferring food");
                order.courier = Probe.Courier; order.deliveryJob = 123; order.pickedUp = true;
                manager.ReleaseCourier(order);
                Check(manager.Valid(order) && manager.Get(p) == order && order.created == created, "Courier failure cancelled/renewed order");
                Probe.Courier.drafter.Drafted = false;
                if (order.courier == null)
                {
                    var job = new WorkGiver_DeliverMeal().JobOnThing(Probe.Courier, p);
                    Check(job != null, "Departed worker cannot receive delivery");
                    Probe.Courier.jobs.StartJob(job, JobCondition.InterruptForced);
                }
                Check(order.courier == Probe.Courier, "No courier after restart");
            }
            else if (command == "hungerDelivered")
            {
                Check(manager.Get(p) == null && (MealDelivery.HasMeal(p) || p.needs.food.CurLevelPercentage > 0.8f), "Mobile recipient did not receive meal");
            }
            else if (command == "hungerTimeout")
            {
                p.inventory.innerContainer.ClearAndDestroyContents();
                manager.orders.Clear(); manager.timedOut.Clear();
                p.needs.food.CurLevelPercentage = 0.29f;
                var order = new MealOrder { receiver = p, created = Find.TickManager.TicksGame - 2500 };
                manager.orders.Add(order);
                Check(!MealDelivery.ShouldWait(p), "Timeout still blocks food");
            }
            else if (command == "hungerTimeoutAssert")
            {
                Check(manager.Get(p) == null && manager.timedOut.Contains(p) && !manager.CanOrder(p), "Timeout can repeatedly reorder");
                p.needs.food.CurLevelPercentage = 0.9f; manager.Observe(p);
                Check(!manager.timedOut.Contains(p), "Eating did not reset hunger episode");
                p.needs.food.CurLevelPercentage = 0.29f;
                Check(manager.CanOrder(p), "Next hunger cannot order");
                manager.Request(p); p.needs.food.CurLevelPercentage = 0.9f;
                Check(!manager.Valid(manager.Get(p)), "Satiated pawn retains order");
            }
            else if (command == "hungerCarried")
            {
                p.jobs.StopAll(); manager.orders.Clear(); manager.timedOut.Clear();
                p.inventory.innerContainer.ClearAndDestroyContents();
                p.inventory.innerContainer.TryAdd(ThingMaker.MakeThing(ThingDefOf.MealSimple));
                p.needs.food.CurLevelPercentage = 0.29f;
                object[] args = { null, false };
                var result = (ThinkResult)AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob").Invoke(p.jobs, args);
                Check(MealDelivery.WorkJob(result.Job) && manager.Get(p) == null, "Personal meal failed next-work selection or created duplicate order");
                JobMaker.ReturnToPool(result.Job);
            }
            else if (command == "hungerFallback")
            {
                Probe.Courier.drafter.Drafted = true;
                p.jobs.StopAll(); p.inventory.innerContainer.ClearAndDestroyContents();
                manager.orders.Clear(); manager.timedOut.Clear();
                p.needs.food.CurLevelPercentage = 0.29f;
                var priorities = DefDatabase<WorkTypeDef>.AllDefs.ToDictionary(d => d, d => p.workSettings.GetPriority(d));
                foreach (var d in priorities.Keys) p.workSettings.SetPriority(d, 0);
                object[] args = { null, false };
                var result = (ThinkResult)AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob").Invoke(p.jobs, args);
                Check(result.SourceNode is JobGiver_GetFood && manager.Get(p) == null, "No work still overrides food");
                JobMaker.ReturnToPool(result.Job);
                foreach (var item in priorities) p.workSettings.SetPriority(item.Key, item.Value);
                manager.Request(p);
                p.needs.food.CurLevelPercentage = 0.05f;
                Check(manager.Valid(manager.Get(p)) && !MealDelivery.ShouldWait(p) && !manager.CanOrder(p), "Urgent hunger waits / deletes order");
                p.drafter.Drafted = true;
                Check(!manager.Valid(manager.Get(p)), "Unreceivable drafted pawn retains valid order");
            }
            else throw new Exception(command);
            File.AppendAllText(Probe.Folder + "hunger-results.txt", command + " PASS\n");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeskLunch
{
    public sealed class MealOrder : IExposable
    {
        public Pawn receiver, courier;
        public Thing station;
        public int lastWork, created, deliveryJob = -1, waitStarted = -1;
        public bool pickedUp;
        // Legacy station/work fields remain readable, but no longer govern an order.
        public bool WaitWithinLimit(int now, int tolerance)
        {
            return now - created < tolerance;
        }
        public void ExposeData()
        {
            Scribe_References.Look(ref receiver, "receiver");
            Scribe_References.Look(ref courier, "courier");
            Scribe_References.Look(ref station, "station");
            Scribe_Values.Look(ref lastWork, "lastWork");
            Scribe_Values.Look(ref created, "created");
            Scribe_Values.Look(ref deliveryJob, "deliveryJob", -1);
            Scribe_Values.Look(ref waitStarted, "waitStarted", -1);
            Scribe_Values.Look(ref pickedUp, "pickedUp");
        }
    }

    public sealed class MealDelivery : MapComponent
    {
        public List<MealOrder> orders = new List<MealOrder>();
        public List<Pawn> timedOut = new List<Pawn>();
        public MealDelivery(Map map) : base(map) { }
        public static MealDelivery For(Pawn pawn) => pawn?.Map?.GetComponent<MealDelivery>();
        public MealOrder Get(Pawn pawn) => orders.FirstOrDefault(o => o.receiver == pawn);
        public bool Claim(MealOrder order, Pawn courier, int jobId)
        {
            if (!orders.Contains(order) || order.courier != null) return false;
            order.courier = courier;
            order.deliveryJob = jobId;
            return true;
        }
        static int Now => Find.TickManager.TicksGame;
        public static bool Eligible(Pawn p) => p != null && p.Spawned && MealWorker.Supported(p)
            && !p.Dead && !p.Downed && !p.Drafted && !p.InMentalState;
        public static bool MealFor(Pawn p, Thing t) => t != null && !t.Destroyed
            && t.def.IsNutritionGivingIngestible && !t.def.IsDrug
            && (t.def.ingestible.foodType & FoodTypeFlags.Meal) != 0
            && t.IngestibleNow && !t.IsNotFresh() && p.WillEat(t)
            && FoodUtility.NutritionForEater(p, t) > 0;
        public static bool HasMeal(Pawn p) => p.inventory?.innerContainer.Any(t => MealFor(p, t) && !MealCargo.IsCargo(p, t)) == true;
        public static bool Deliverable(Pawn receiver, Pawn courier, Thing food) => MealFor(receiver, food)
            && receiver.WillEat(food, courier)
            && (food.IsSociallyProper(courier) || food.IsSociallyProper(receiver, receiver.IsPrisonerOfColony, !courier.IsAnimal));
        public static bool Hungry(Pawn p) => p?.needs?.food != null
            && p.needs.food.CurLevelPercentage <= p.RaceProps.FoodLevelPercentageWantEat;
        public static bool WorkJob(Job job) => job?.def?.driverClass != null
            && (job.targetA.Thing is Building_WorkTable && typeof(JobDriver_DoBill).IsAssignableFrom(job.def.driverClass)
                || job.targetA.Thing is Building_ResearchBench && typeof(JobDriver_Research).IsAssignableFrom(job.def.driverClass));
        public bool CanOrder(Pawn p) => DeskLunchMod.Settings.deliveries && Eligible(p)
            && MealSchedule.AllowsWorkMeals(p) && Hungry(p) && !HasMeal(p) && !timedOut.Contains(p)
            && (Get(p) == null || Get(p).WaitWithinLimit(Now, DeskLunchMod.Settings.deliveryWait))
            && p.needs.food.CurLevelPercentage > p.RaceProps.FoodLevelPercentageWantEat * 0.4f
            && DeskLunchMod.Settings.deliveryWait > 0 && MealCouriers.HasCourier(p);
        public bool CanChooseWork(Pawn p) => CanOrder(p)
            || DeskLunchMod.Settings.enabled && !DeskLunchMod.Settings.preferLunchBreak && Eligible(p)
            && MealSchedule.AllowsWorkMeals(p) && Hungry(p) && HasMeal(p)
            && p.needs.food.CurLevelPercentage > p.RaceProps.FoodLevelPercentageWantEat * 0.4f;
        public void Observe(Pawn p)
        {
            if (!Hungry(p)) timedOut.Remove(p);
            if (WorkJob(p.CurJob)) Request(p);
        }
        public void Request(Pawn p)
        {
            if (Get(p) == null && CanOrder(p)) orders.Add(new MealOrder { receiver = p, created = Now });
        }
        public bool Valid(MealOrder o)
        {
            Pawn p = o.receiver;
            return DeskLunchMod.Settings.deliveries && Eligible(p) && p.Map == map && !HasMeal(p)
                && Hungry(p) && o.WaitWithinLimit(Now, DeskLunchMod.Settings.deliveryWait);
        }
        public bool InTransit(MealOrder o) => o.pickedUp && o.courier != null && o.courier.Spawned
            && o.courier.Map == map && !o.courier.Downed && !o.courier.Dead && !o.courier.Drafted
            && o.courier.CurJob?.loadID == o.deliveryJob
            && (o.courier.jobs.curDriver as JobDriver_DeliverMeal)?.HasCargoFor(o.receiver) == true;
        public static bool ShouldWait(Pawn p)
        {
            var manager = For(p);
            manager?.Observe(p);
            var o = manager?.Get(p);
            if (o == null) return false;
            if (p.needs?.food != null && p.needs.food.CurLevelPercentage >= p.RaceProps.FoodLevelPercentageWantEat)
                return false;
            // Only defer food during work time and non-urgent hunger; keep the order otherwise.
            return manager.Valid(o) && MealSchedule.AllowsWorkMeals(p) && MealCouriers.HasCourier(p)
                && p.needs.food.CurLevelPercentage > p.RaceProps.FoodLevelPercentageWantEat * 0.4f;
        }
        public override void MapComponentTick()
        {
            if (Now % 60 != 0) return;
            timedOut.RemoveAll(p => p == null || !p.Spawned || p.Map != map || !Hungry(p));
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                if (MealWorker.Supported(p)) Observe(p);
            foreach (var o in orders.ToArray())
            {
                if (!Valid(o))
                {
                    if (!o.WaitWithinLimit(Now, DeskLunchMod.Settings.deliveryWait) && o.receiver != null && !timedOut.Contains(o.receiver))
                        timedOut.Add(o.receiver);
                    orders.Remove(o);
                }
                else if (o.courier != null && o.courier.CurJob?.loadID != o.deliveryJob)
                {
                    ReleaseCourier(o);
                }
            }
        }
        public void ReleaseCourier(MealOrder o)
        { o.courier = null; o.deliveryJob = -1; o.pickedUp = false; }
        public override void ExposeData()
        {
            Scribe_Collections.Look(ref orders, "mealsAtWorkDeliveryOrders", LookMode.Deep);
            Scribe_Collections.Look(ref timedOut, "mealsAtWorkTimedOut", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && orders == null) orders = new List<MealOrder>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit && timedOut == null) timedOut = new List<Pawn>();
        }
    }

    public sealed class WorkGiver_DeliverMeal : WorkGiver_Scanner
    {
        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) =>
            MealDelivery.For(pawn).orders.Where(o => o.courier == null).Select(o => (Thing)o.receiver).ToArray();
        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!(t is Pawn receiver) || !MealCouriers.CanServe(pawn, receiver)) return null;
            var manager = MealDelivery.For(pawn);
            var order = manager.Get(receiver);
            if (order == null || order.courier != null || !manager.Valid(order)
                || !pawn.CanReach(receiver, PathEndMode.Touch, Danger.None)) return null;
            // Physical prepared meals only; never create a dispenser/cooking job for a courier.
            Thing meal = FindMeal(pawn, receiver);
            if (meal == null) return null;
            Job job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("MealsAtWork_DeliverMeal"), meal, receiver);
            job.count = 1;
            return job;
        }
        public static Thing FindMeal(Pawn pawn, Pawn receiver, Dictionary<Thing, int> planned = null, float plannedMass = 0)
        {
            if (pawn.inventory == null || pawn.carryTracker.CarriedThing != null) return null;
            return pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree)
                .Where(f => f.Spawned && MealDelivery.Deliverable(receiver, pawn, f) && !f.IsForbidden(pawn)
                    && pawn.carryTracker.MaxStackSpaceEver(f.def) > (planned != null && planned.TryGetValue(f, out int n) ? n : 0)
                    && (!MassUtility.CanEverCarryAnything(pawn) || MassUtility.FreeSpace(pawn) >= plannedMass + f.GetStatValue(StatDefOf.Mass))
                    && MealCouriers.FreeVolume(pawn) >= f.def.VolumePerUnit + (planned?.Sum(kv => kv.Key.def.VolumePerUnit * kv.Value) ?? 0)
                    && pawn.CanReserve(f, 1, 1 + (planned != null && planned.TryGetValue(f, out int count) ? count : 0))
                    && pawn.CanReach(f, PathEndMode.ClosestTouch, Danger.None))
                .OrderBy(f => f.Position.DistanceToSquared(pawn.Position)).FirstOrDefault();
        }
    }

}

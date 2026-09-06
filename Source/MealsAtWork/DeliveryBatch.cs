using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace DeskLunch
{
    public sealed class MealDeliveryStop : IExposable
    {
        public Pawn receiver;
        public Thing source, cargo;
        public void ExposeData()
        {
            Scribe_References.Look(ref receiver, "receiver");
            Scribe_References.Look(ref source, "source");
            Scribe_References.Look(ref cargo, "cargo");
        }
    }

    public sealed class JobDriver_DeliverMeal : JobDriver
    {
        public const int MaxBatchSize = 6;
        public List<MealDeliveryStop> stops = new List<MealDeliveryStop>();
        bool delivering;
        int travelTicks;
        Thing travellingTo;
        MealDelivery Manager => MealDelivery.For(pawn);
        bool Owns(MealDeliveryStop stop)
        {
            var order = Manager?.Get(stop.receiver);
            return order != null && order.courier == pawn && order.deliveryJob == job.loadID;
        }
        public bool IsCargo(Thing food) => stops.Any(s => s.cargo == food);
        public bool HasCargoFor(Pawn receiver) => stops.Any(s => s.receiver == receiver
            && s.cargo != null && pawn.inventory.innerContainer.Contains(s.cargo) && MealDelivery.MealFor(receiver, s.cargo));

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref stops, "mealDeliveryStops", LookMode.Deep);
            Scribe_Values.Look(ref delivering, "deliveringBatch");
            Scribe_Values.Look(ref travelTicks, "batchTravelTicks");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && stops == null) stops = new List<MealDeliveryStop>();
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (Manager == null || pawn.inventory == null || pawn.carryTracker.CarriedThing != null) return false;
            float mass = 0;
            var planned = new Dictionary<Thing, int>();
            var candidates = Manager.orders.Where(o => o.courier == null && Manager.Valid(o))
                .OrderBy(o => o.receiver == job.targetB.Pawn ? -1 : o.receiver.Position.DistanceToSquared(pawn.Position)).ToArray();
            foreach (var order in candidates)
            {
                if (stops.Count >= MaxBatchSize) break;
                if (!MealCouriers.CanServe(pawn, order.receiver)) continue;
                var food = WorkGiver_DeliverMeal.FindMeal(pawn, order.receiver, planned, mass);
                if (food == null) continue;
                stops.Add(new MealDeliveryStop { receiver = order.receiver, source = food });
                planned[food] = (planned.TryGetValue(food, out int count) ? count : 0) + 1;
                mass += food.GetStatValue(StatDefOf.Mass);
            }
            foreach (var group in stops.GroupBy(s => s.source).ToArray())
            {
                if (!pawn.Reserve(group.Key, job, 1, group.Count(), null, false))
                    stops.RemoveAll(s => s.source == group.Key);
            }
            foreach (var stop in stops.ToArray())
                if (!Manager.Claim(Manager.Get(stop.receiver), pawn, job.loadID)) stops.Remove(stop);
            return stops.Count > 0;
        }

        void DropCargo(MealDeliveryStop stop)
        {
            if (stop.cargo != null && pawn.Spawned && pawn.inventory.innerContainer.Contains(stop.cargo))
                pawn.inventory.innerContainer.TryDrop(stop.cargo, pawn.Position, pawn.Map, ThingPlaceMode.Near, out Thing _);
        }
        void RemoveStop(MealDeliveryStop stop)
        {
            if (Owns(stop)) Manager.ReleaseCourier(Manager.Get(stop.receiver));
            DropCargo(stop);
            stops.Remove(stop);
        }
        void FinishBatch()
        {
            foreach (var stop in stops.ToArray()) RemoveStop(stop);
            if (pawn.Spawned && pawn.carryTracker.CarriedThing != null)
                pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
        }
        bool WalkTo(Thing target, int delta)
        {
            if (pawn.Position.AdjacentTo8WayOrInside(target.Position))
            { pawn.pather.StopDead(); travellingTo = null; travelTicks = 0; return true; }
            if (travellingTo != target) { travellingTo = target; travelTicks = 0; }
            travelTicks += delta;
            if (!pawn.pather.Moving || pawn.pather.Destination.Thing != target)
                pawn.pather.StartPath(target, PathEndMode.Touch);
            return false;
        }
        void TickBatch(int delta)
        {
            foreach (var stop in stops.ToArray())
            {
                if (!Owns(stop) || !Manager.Valid(Manager.Get(stop.receiver)) || !MealCouriers.CanServe(pawn, stop.receiver)
                    || stop.cargo != null && (!pawn.inventory.innerContainer.Contains(stop.cargo)
                        || !MealDelivery.Deliverable(stop.receiver, pawn, stop.cargo))) RemoveStop(stop);
            }
            if (stops.Count == 0) { EndJobWith(JobCondition.Succeeded); return; }
            if (!delivering)
            {
                var pending = stops.Where(s => s.cargo == null).ToArray();
                foreach (var stop in pending)
                    if (stop.source == null || !stop.source.Spawned || stop.source.IsForbidden(pawn)
                        || !MealDelivery.Deliverable(stop.receiver, pawn, stop.source)
                        || !pawn.CanReach(stop.source, PathEndMode.Touch, Danger.None)) RemoveStop(stop);
                var next = stops.Where(s => s.cargo == null).OrderBy(s => s.source.Position.DistanceToSquared(pawn.Position)).FirstOrDefault();
                if (next == null) { delivering = true; travellingTo = null; travelTicks = 0; return; }
                job.targetA = next.source;
                job.targetB = next.receiver;
                if (!WalkTo(next.source, delta))
                {
                    if (travelTicks > 2500) foreach (var s in stops.Where(s => s.source == next.source).ToArray()) RemoveStop(s);
                    return;
                }
                var group = stops.Where(s => s.cargo == null && s.source == next.source).ToArray();
                int count = System.Math.Min(group.Length, MealCouriers.PickupLimit(pawn, next.source));
                if (count <= 0) { foreach (var s in group) RemoveStop(s); return; }
                int taken = pawn.carryTracker.TryStartCarry(next.source, count, false);
                for (int i = 0; i < group.Length; i++)
                {
                    var stop = group[i];
                    if (i >= taken || pawn.carryTracker.innerContainer.TryTransferToContainer(pawn.carryTracker.CarriedThing,
                        pawn.inventory.innerContainer, 1, out Thing cargo, false) != 1) { RemoveStop(stop); continue; }
                    stop.cargo = cargo;
                    Manager.Get(stop.receiver).pickedUp = true;
                }
                if (pawn.carryTracker.CarriedThing != null)
                    pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                return;
            }
            var destination = stops.OrderBy(s => s.receiver.Position.DistanceToSquared(pawn.Position)).First();
            job.targetB = destination.receiver;
            if (!WalkTo(destination.receiver, delta))
            { if (travelTicks > 2500) RemoveStop(destination); return; }
            if (MealCargo.TransferDelivery(pawn, destination.receiver, destination.cargo))
            {
                Manager.orders.Remove(Manager.Get(destination.receiver));
                stops.Remove(destination);
            }
            else RemoveStop(destination);
        }
        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Drafted || pawn.InMentalState);
            AddFinishAction(_ => FinishBatch());
            var route = ToilMaker.MakeToil("CollectAndDeliverMeals");
            route.defaultCompleteMode = ToilCompleteMode.Never;
            route.tickIntervalAction = TickBatch;
            yield return route;
        }
    }
}

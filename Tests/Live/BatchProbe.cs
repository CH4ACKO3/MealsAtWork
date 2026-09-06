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
    static class BatchProbe
    {
        static void Check(bool value, string label)
        {
            if (!value) throw new Exception(label);
            File.AppendAllText(Probe.Folder + "batch-results.txt", "PASS " + label + "\n");
        }
        static Pawn Named(string name) => Find.CurrentMap.mapPawns.AllPawnsSpawned.First(p => p.Name?.ToStringShort == name);
        static void Hungry(Pawn p) { p.inventory.innerContainer.ClearAndDestroyContents(); p.needs.food.CurLevelPercentage = 0.25f; }
        public static void Run(string command)
        {
            var worker = Probe.Worker;
            var courier = Probe.Courier;
            var manager = MealDelivery.For(worker);
            if(command == "batchInterrupt")
            {
                var driver = (JobDriver_DeliverMeal)courier.jobs.curDriver;
                var records = manager.orders.Where(o=>o.courier==courier).ToArray();
                var created = records.Select(o=>o.created).ToArray();
                int cargo = driver.stops.Count;
                courier.drafter.Drafted=true;
                Check(records.All(o=>o.courier==null && !o.pickedUp),"interruption releases every claim");
                Check(driver.stops.Count==0,"interruption clears batch route");
                Check(records.Select(o=>o.created).SequenceEqual(created),"interruption preserves order deadlines");
                Check(courier.inventory.innerContainer.Count==0,"interruption returns every carried meal to map");
                courier.drafter.Drafted=false;
                var giver=(WorkGiver_DeliverMeal)DefDatabase<WorkGiverDef>.GetNamed("MealsAtWork_DeliverMeal").Worker;
                if (!(courier.jobs.curDriver is JobDriver_DeliverMeal))
                {
                    var retry=giver.JobOnThing(courier,worker);
                    Check(retry!=null,"released orders can be claimed again");
                    courier.jobs.StartJob(retry,JobCondition.InterruptForced);
                }
                Check(((JobDriver_DeliverMeal)courier.jobs.curDriver).stops.Count==cargo,"retry claims the complete batch");
                return;
            }
            if(command == "batchDebug")
            {
                var dog = Named("BatchDog");
                var giver = (WorkGiver_DeliverMeal)DefDatabase<WorkGiverDef>.GetNamed("MealsAtWork_DeliverMeal").Worker;
                foreach(var o in manager.orders)
                {
                    File.AppendAllText(Probe.Folder+"batch-results.txt", $"DEBUG {o.receiver.Name} valid={manager.Valid(o)} serve={MealCouriers.CanServe(dog,o.receiver)} claimed={o.courier} mass={MassUtility.FreeSpace(dog)} job={giver.JobOnThing(dog,o.receiver)}\n");
                    foreach(var f in dog.Map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree).Where(f=>f.def.defName.StartsWith("Meal")))
                        File.AppendAllText(Probe.Folder+"batch-results.txt", $"food={f} deliver={MealDelivery.Deliverable(o.receiver,dog,f)} forbidden={f.IsForbidden(dog)} reserve={dog.CanReserve(f,1,1)} reach={dog.CanReach(f,PathEndMode.ClosestTouch,Danger.None)} carry={dog.carryTracker.MaxStackSpaceEver(f.def)}\n");
                }
                return;
            }
            if (command == "batchMatrix")
            {
                File.WriteAllText(Probe.Folder + "batch-results.txt", "Courier eligibility matrix\n");
                Hungry(worker); manager.orders.Clear();
                courier.workSettings.SetPriority(WorkTypeDefOf.Hauling, 0);
                Check(!MealCouriers.CanServe(worker,worker) && !MealCouriers.HasCourier(worker), "self hauling is not a courier");
                manager.Request(worker);
                Check(manager.Get(worker) == null && !manager.CanChooseWork(worker), "no courier: no order or food deferral");
                worker.workSettings.SetPriority(WorkTypeDefOf.Hauling, 0);
                courier.workSettings.SetPriority(WorkTypeDefOf.Hauling, 1);
                Check(MealCouriers.HasCourier(worker), "enabled human hauler");
                courier.drafter.Drafted = true;
                Check(!MealCouriers.HasCourier(worker), "drafted hauler excluded");
                courier.drafter.Drafted = false;
                var animal = PawnGenerator.GeneratePawn(PawnKindDef.Named("Husky"), Faction.OfPlayer);
                GenSpawn.Spawn(animal, new IntVec3(138,0,108), worker.Map); animal.Name = new NameSingle("BatchAnimal");
                courier.workSettings.SetPriority(WorkTypeDefOf.Hauling, 0);
                Check(!MealCouriers.CanServe(animal,worker), "untrained animal excluded");
                animal.training.Train(DefDatabase<TrainableDef>.GetNamed("Haul"), courier, true);
                Check(MealCouriers.CanServe(animal,worker), "trained animal courier");
                animal.DeSpawn();
                worker.health.AddHediff(HediffDefOf.MechlinkImplant);
                var mech = PawnGenerator.GeneratePawn(PawnKindDef.Named("Mech_Lifter"),Faction.OfPlayer);
                GenSpawn.Spawn(mech,new IntVec3(137,0,108),worker.Map);
                worker.relations.AddDirectRelation(PawnRelationDefOf.Overseer,mech);
                worker.mechanitor.AssignPawnControlGroup(mech);
                Check(MealCouriers.CanServe(mech,worker), "controlled hauling mech");
                mech.GetMechControlGroup().SetWorkMode(MechWorkModeDefOf.Recharge);
                Check(!MealCouriers.CanServe(mech,worker), "recharging mech excluded");
                mech.DeSpawn();
                courier.workSettings.SetPriority(WorkTypeDefOf.Hauling, 1);
                manager.Request(worker);
                Check(manager.Get(worker) != null, "courier available creates order");
                courier.drafter.Drafted = true;
                Check(!MealDelivery.ShouldWait(worker) && manager.Get(worker) != null, "couriers gone: keep order and resume food search");
                courier.drafter.Drafted = false;
                manager.orders.Clear();
                return;
            }
            if (command == "batchStart" || command == "batchAnimalStart")
            {
                manager.orders.Clear();
                foreach(var p in worker.Map.mapPawns.FreeColonists.ToArray()) { p.jobs.StopAll(); if(p != worker && p != courier) p.drafter.Drafted=true; }
                Hungry(worker);
                worker.Position = new IntVec3(103,0,103); worker.Notify_Teleported();
                var second = Probe.SpawnPawn("BatchSecond",new IntVec3(108,0,103)); Hungry(second);
                var third = Probe.SpawnPawn("BatchThird",new IntVec3(112,0,103)); Hungry(third);
                var special = Current.Game.foodRestrictionDatabase.MakeNewFoodRestriction();
                special.filter.SetDisallowAll(); special.filter.SetAllow(ThingDefOf.MealSurvivalPack,true);
                third.foodRestriction.CurrentFoodPolicy=special;
                var food = ThingMaker.MakeThing(ThingDefOf.MealSurvivalPack); food.stackCount=10;
                GenSpawn.Spawn(food,new IntVec3(139,0,106),worker.Map);
                Pawn carrier = courier;
                courier.Position=new IntVec3(140,0,105); courier.Notify_Teleported();
                if(command=="batchAnimalStart")
                {
                    courier.drafter.Drafted=true;
                    carrier=PawnGenerator.GeneratePawn(PawnKindDef.Named("Husky"),Faction.OfPlayer);
                    carrier.Name=new NameSingle("BatchDog");
                    GenSpawn.Spawn(carrier,new IntVec3(140,0,105),worker.Map);
                    carrier.training.Train(DefDatabase<TrainableDef>.GetNamed("Haul"),worker,true);
                }
                foreach(var p in new[]{worker,second,third})
                {
                    manager.Request(p);
                    Check(manager.Get(p)!=null,"order created for "+p.Name.ToStringShort);
                    p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.Wait,5000),JobCondition.InterruptForced);
                }
                var giver=(WorkGiver_DeliverMeal)DefDatabase<WorkGiverDef>.GetNamed("MealsAtWork_DeliverMeal").Worker;
                var job=command=="batchAnimalStart"
                    ? (Job)AccessTools.Method(typeof(JobGiver_Haul),"TryGiveJob").Invoke(new JobGiver_Haul(),new object[]{carrier})
                    : giver.JobOnThing(carrier,worker);
                Check(job!=null,"batch job proposed");
                carrier.jobs.StartJob(job,JobCondition.InterruptForced);
                var driver=carrier.jobs.curDriver as JobDriver_DeliverMeal;
                Check(driver?.stops.Count==3,"one courier claims three orders");
                Check(driver.stops.Select(s=>s.source).Distinct().All(s=>carrier.Map.reservationManager.ReservedBy(s,carrier,driver.job)),
                    "meal sources reserved before pickup");
                Check(driver.stops.Single(s=>s.receiver==third).source.def==ThingDefOf.MealSurvivalPack,"individual dietary policy");
                return;
            }
            var active = worker.Map.mapPawns.AllPawnsSpawned.Select(p=>p.jobs?.curDriver).OfType<JobDriver_DeliverMeal>().FirstOrDefault();
            if(command=="batchPicked")
            {
                Check(active!=null && active.stops.Count==3 && active.stops.All(s=>s.cargo!=null),"collected all three meals before delivery");
                Check(active.stops.All(s=>MealCargo.IsCargo(active.pawn,s.cargo)),"delivery inventory marked as cargo");
                Check(active.stops.Select(s=>s.source).Distinct().All(s=>!active.pawn.Map.reservationManager.ReservedBy(s,active.pawn,active.job)),
                    "meal source reservations released after pickup");
                var shrunken = ThingMaker.MakeThing(ThingDefOf.MealSurvivalPack);
                shrunken.stackCount = 1;
                GenSpawn.Spawn(shrunken, active.pawn.Position, active.pawn.Map);
                var first = new MealDeliveryStop { source=shrunken };
                var second = new MealDeliveryStop { source=shrunken };
                active.stops.Add(first); active.stops.Add(second);
                Check(active.pawn.Reserve(shrunken,active.job,1,1,null,false),"shrunken source setup reservation");
                AccessTools.Method(typeof(JobDriver_DeliverMeal),"ResizeSourceReservation").Invoke(active,new object[]{shrunken});
                Check(!active.stops.Contains(first) && !active.stops.Contains(second)
                    && !active.pawn.Map.reservationManager.ReservedBy(shrunken,active.pawn,active.job),
                    "failed source re-reservation retained unreserved stops");
                shrunken.Destroy();
            }
            if(command=="batchCancel") { Named("BatchSecond").drafter.Drafted=true; }
            if(command=="batchDone")
            {
                Check(MealDelivery.HasMeal(worker),"first recipient received meal");
                Check(MealDelivery.HasMeal(Named("BatchThird")),"third recipient received policy meal");
                Check(manager.Get(worker)==null && manager.Get(Named("BatchThird"))==null,"delivered orders cleared");
                Check(!worker.Map.mapPawns.AllPawnsSpawned.Select(p=>p.jobs?.curDriver).OfType<JobDriver_DeliverMeal>().Any(d=>d.stops.Count>0),"batch finished without orphan claims");
            }
        }
    }
}

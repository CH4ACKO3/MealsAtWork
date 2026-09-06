using System;
using System.IO;
using System.Linq;
using DeskLunch;
using RimWorld;
using Verse;
using Verse.AI;
namespace MealsLive
{
    static class MechProbe
    {
        static Pawn Mech => Find.CurrentMap.mapPawns.AllPawnsSpawned.First(p=>p.def.defName=="MealsTest_FoodMech");
        static void Check(bool b,string message) { if(!b) throw new Exception(message); }
        public static void Run(string command)
        {
            if(command=="mechSetup")
            {
                var worker=Probe.Worker; worker.drafter.Drafted=true; worker.Position=new IntVec3(103,0,102);worker.Notify_Teleported();
                var vanilla=PawnGenerator.GeneratePawn(PawnKindDef.Named("Mech_Fabricor"),Faction.OfPlayer);
                Check(vanilla.needs.food==null && !MealWorker.Supported(vanilla),"Vanilla energy mech admitted");
                var animal=PawnGenerator.GeneratePawn(PawnKindDef.Named("Cow"),Faction.OfPlayer);
                Check(!MealWorker.Supported(animal),"Animal worker admitted");
                var p=PawnGenerator.GeneratePawn(PawnKindDef.Named("MealsTest_FoodMech"),Faction.OfPlayer);
                GenSpawn.Spawn(p,new IntVec3(108,0,104),worker.Map);
                Check(p.RaceProps.IsMechanoid && !p.RaceProps.Humanlike && MealDelivery.Eligible(p),"Food mech rejected");
                Check(!LunchUtility.ShouldApplyPenalty(p),"Moodless mech has mood penalty");
                p.needs.food.CurLevelPercentage=0.29f;
                // The saved fixture contains an unfinished garment authored by Worker.
                // Give this new pawn an independent bill and fresh material.
                Probe.Bench.BillStack.Clear();
                foreach(var old in worker.Map.listerThings.AllThings.OfType<UnfinishedThing>().ToArray()) old.Destroy();
                var recipe=DefDatabase<RecipeDef>.AllDefs.First(r=>r.products.Any(x=>x.thingDef.defName=="Apparel_Pants")&&r.defName.StartsWith("Make_"));
                Probe.Bench.BillStack.AddBill(recipe.MakeNewBill());
                var cloth=ThingMaker.MakeThing(ThingDefOf.Cloth);cloth.stackCount=75;GenSpawn.Spawn(cloth,new IntVec3(107,0,105),worker.Map);
                var giver=(WorkGiver_DoBill)DefDatabase<WorkGiverDef>.AllDefs.First(d=>d.workType==DefDatabase<WorkTypeDef>.GetNamed("Tailoring") && d.giverClass==typeof(WorkGiver_DoBill)).Worker;
                var job=giver.JobOnThing(p,Probe.Bench,true);Check(job!=null,"Mech cannot get crafting job");
                p.jobs.TryTakeOrderedJob(job,JobTag.MiscWork);
                MealDelivery.For(p).Observe(p);Check(MealDelivery.For(p).Get(p)!=null && MealDelivery.ShouldWait(p),"Mech preparation does not order/wait");
                Probe.Courier.drafter.Drafted=false;
                var courierJob=new WorkGiver_DeliverMeal().JobOnThing(Probe.Courier,p);
                if(courierJob!=null) Probe.Courier.jobs.StartJob(courierJob,JobCondition.InterruptForced);
            }
            else if(command=="mechInspect")
            {
                var p=Mech;var s=LunchUtility.State(p.jobs.curDriver);
                File.AppendAllText(Probe.Folder+"mech-results.txt","tick="+Find.TickManager.TicksGame+" job="+p.CurJob+" meal="+s.meal+" remain="+s.remaining+" food="+p.needs.food.CurLevelPercentage+" order="+(MealDelivery.For(p).Get(p)!=null)+" session="+(LunchBreakUtility.Manager(p).Get(p)!=null)+"\n");
                return;
            }
            else if(command=="mechDone")
            {
                var p=Mech;
                Check(p.needs.food.CurLevelPercentage>0.8f,"Mech failed to eat delivered meal");
                Check(!MealDelivery.HasMeal(p)&&MealDelivery.For(p).Get(p)==null,"Mech delivery not consumed/closed");
                Check(!LunchUtility.HasActiveMeal(p.jobs.curDriver)&&LunchBreakUtility.Manager(p).Get(p)==null,"Mech meal reservation leaked");
                Check(p.needs.mood==null,"Fixture unexpectedly has mood");
            }
            File.AppendAllText(Probe.Folder+"mech-results.txt",command+" PASS\n");
        }
    }
}


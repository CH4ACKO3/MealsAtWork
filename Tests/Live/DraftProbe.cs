using System.IO;
using System.Linq;
using DeskLunch;
using RimWorld;
using Verse;
using Verse.AI;
namespace MealsLive
{
    static class DraftProbe
    {
        public static void Change(string command)
        {
            var p = Find.CurrentMap.mapPawns.FreeColonistsSpawned.First(x=>x.thingIDNumber==770);
            if(command=="draftTestOn") p.drafter.Drafted=true;
            if(command=="draftTestOff") p.drafter.Drafted=false;
            if(command=="draftTestWork")
            {
                var bench=Find.CurrentMap.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>().First(t=>t.def.defName=="TableStonecutter");
                var job=((WorkGiver_Scanner)DefDatabase<WorkGiverDef>.GetNamed("DoBillsStonecut").Worker).JobOnThing(p,bench,true);
                if(job==null) throw new System.Exception("Cannot resume stonecutting");
                p.jobs.StartJob(job,JobCondition.InterruptForced);
            }
            Run();
        }
        public static void Run()
        {
            var map = Find.CurrentMap;
            var manager = map.GetComponent<MealDelivery>();
            var lines = new System.Collections.Generic.List<string>();
            foreach (var o in manager.orders)
            {
                lines.Add("tick="+Find.TickManager.TicksGame+" order " + o.receiver + " valid=" + manager.Valid(o) + " drafted=" + o.receiver.Drafted + " food=" + o.receiver.needs.food.CurLevelPercentage+" assigned="+o.courier+" picked="+o.pickedUp);
                foreach (var c in map.mapPawns.FreeColonistsSpawned.Where(p=>p!=o.receiver))
                {
                    var job = new WorkGiver_DeliverMeal().JobOnThing(c, o.receiver);
                    lines.Add("courier " + c + " current="+c.CurJob+" idle="+c.CurJob?.def.isIdle+" haul=" + c.workSettings.GetPriority(WorkTypeDefOf.Hauling) + " targetForbidden=" + o.receiver.IsForbidden(c) + " reach=" + c.CanReach(o.receiver,PathEndMode.Touch,Danger.None) + " job=" + job);
                    if(job!=null) JobMaker.ReturnToPool(job);
                    foreach(var f in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree).Where(t=>t.def.IsNutritionGivingIngestible && (t.def.ingestible.foodType & FoodTypeFlags.Meal)!=0 && !t.IsForbidden(c)))
                        lines.Add("food "+f+" suitable="+MealDelivery.Deliverable(o.receiver,c,f)+" reserve="+c.CanReserve(f,1,1)+" reach="+c.CanReach(f,PathEndMode.ClosestTouch,Danger.None));
                }
            }
            File.WriteAllLines(Probe.Folder+"draft-inspect.txt", lines);
            File.AppendAllLines(Probe.Folder+"draft-regression.txt", lines);
        }
    }
}

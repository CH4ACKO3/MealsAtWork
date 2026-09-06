using RimWorld;
using Verse;

namespace DeskLunch
{
    public static class MealSchedule
    {
        // Schedule Everything's workstation work assignments (verified against its defs).
        public static bool AllowsWorkMeals(TimeAssignmentDef assignment) => assignment == null
            || assignment.defName == "Anything" || assignment.defName == "Work"
            || assignment.defName == "Mazo_Cook" || assignment.defName == "Mazo_Smith"
            || assignment.defName == "Mazo_Tailor" || assignment.defName == "Mazo_Art"
            || assignment.defName == "Mazo_Craft" || assignment.defName == "Mazo_Research"
            || assignment.GetModExtension<WorkMealScheduleExtension>()?.allowWorkMeals == true;
        public static bool AllowsWorkMeals(Pawn pawn) => AllowsWorkMeals(pawn?.timetable?.CurrentAssignment);
    }
    public sealed class WorkMealScheduleExtension : DefModExtension
    {
        public bool allowWorkMeals;
    }
}

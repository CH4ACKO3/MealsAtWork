using RimWorld;
using Verse;

namespace DeskLunch
{
    public static class MealWorker
    {
        // Food/energy are separate needs. Never give a food need to a vanilla mech.
        // Animal workers (including HardworkingKz) and foreign guests remain excluded.
        public static bool Supported(Pawn pawn) => pawn?.needs?.food != null && pawn.inventory != null
            && (pawn.IsColonistPlayerControlled
                || pawn.RaceProps.IsMechanoid && pawn.Faction == Faction.OfPlayer
                    && pawn.HostFaction == null && !pawn.IsPrisoner);
    }
}

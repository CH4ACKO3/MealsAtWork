using HarmonyLib;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace DeskLunch
{
    public sealed class DeskLunchSettings : ModSettings
    {
        public bool enabled = true;
        public bool moodPenalty = true;
        public bool lunchBreaks = true;
        public bool preferLunchBreak;
        public bool idleWorktables = true;
        public float worktableDistanceWeight = 2f;
        public bool deliveries = true;
        public int deliveryLead = 2500, deliveryGrace = 1250, deliveryWait = 5000;
        private int deliverySettingsVersion = 2;
        public override void ExposeData()
        {
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref moodPenalty, "moodPenalty", true);
            Scribe_Values.Look(ref lunchBreaks, "lunchBreaks", true);
            Scribe_Values.Look(ref preferLunchBreak, "preferLunchBreak");
            Scribe_Values.Look(ref idleWorktables, "idleWorktables", true);
            Scribe_Values.Look(ref worktableDistanceWeight, "worktableDistanceWeight", 2f);
            Scribe_Values.Look(ref deliveries, "deliveries", true);
            Scribe_Values.Look(ref deliveryLead, "deliveryLead", 2500);
            Scribe_Values.Look(ref deliveryGrace, "deliveryGrace", 1250);
            Scribe_Values.Look(ref deliveryWait, "deliveryWait", 5000);
            Scribe_Values.Look(ref deliverySettingsVersion, "deliverySettingsVersion", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) UpgradeDeliverySettings();
        }
        public void UpgradeDeliverySettings()
        {
            if (deliverySettingsVersion < 1 && deliveryWait == 1250) deliveryWait = 2500;
            if (deliverySettingsVersion < 2 && deliveryWait == 2500) deliveryWait = 5000;
            deliverySettingsVersion = 2;
        }
    }

    public sealed class DeskLunchMod : Mod
    {
        public static DeskLunchSettings Settings;
        public DeskLunchMod(ModContentPack content) : base(content)
        {
            // Keep serialized type names and migrate settings after the folder rename.
            string settingsPath = Path.Combine(GenFilePaths.ConfigFolderPath,
                GenText.SanitizeFilename($"Mod_{content.FolderName}_{GetType().Name}.xml"));
            bool migrate = !File.Exists(settingsPath) && content.FolderName != "DeskLunch";
            Settings = GetSettings<DeskLunchSettings>();
            if (migrate)
            {
                var previous = LoadedModManager.ReadModSettings<DeskLunchSettings>("DeskLunch", nameof(DeskLunchMod));
                Settings.enabled = previous.enabled;
                Settings.moodPenalty = previous.moodPenalty;
            }
            new Harmony("ch4acko3.desklunch").PatchAll();
        }
        public override string SettingsCategory() => "Meals at Work";
        public override void DoSettingsWindowContents(Rect rect)
        {
            var listing = new Listing_Standard();
            listing.Begin(rect);
            listing.CheckboxLabeled("DeskLunch.Enabled".Translate(), ref Settings.enabled);
            listing.CheckboxLabeled("DeskLunch.Penalty".Translate(), ref Settings.moodPenalty);
            listing.CheckboxLabeled("MealsAtWork.AllowBreaks".Translate(), ref Settings.lunchBreaks);
            listing.CheckboxLabeled("MealsAtWork.PreferBreak".Translate(), ref Settings.preferLunchBreak);
            listing.CheckboxLabeled("MealsAtWork.IdleTables".Translate(), ref Settings.idleWorktables);
            listing.Label("MealsAtWork.TableDistanceWeight".Translate(Settings.worktableDistanceWeight.ToString("0.0")));
            Settings.worktableDistanceWeight = listing.Slider(Settings.worktableDistanceWeight, 1f, 5f);
            listing.CheckboxLabeled("MealsAtWork.Deliveries".Translate(), ref Settings.deliveries);
            listing.Label("MealsAtWork.DeliveryWait".Translate((Settings.deliveryWait / 2500f).ToString("0.00")));
            Settings.deliveryWait = (int)listing.Slider(Settings.deliveryWait, 0, 5000);
            listing.Gap();
            listing.Label("DeskLunch.Description".Translate());
            listing.End();
        }
    }

    public sealed class Thought_DeskLunch : Thought_Memory
    {
        public override bool ShouldDiscard => !LunchUtility.ShouldApplyPenalty(pawn) || base.ShouldDiscard;
        public override float MoodOffset() => LunchUtility.ShouldApplyPenalty(pawn) ? base.MoodOffset() : 0f;
    }
}

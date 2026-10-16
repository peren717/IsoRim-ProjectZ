using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ProjectZ
{
    // Slabs of any stuff (redesign §9). The game keeps no stuff per terrain cell, so, like its stone tiles, there is one
    // slab TerrainDef per stuff, all in one build-menu dropdown (PZ_UpperFloors). PZ_UpperFloor (XML) is the wood slab,
    // the defName older saves have; the others are made from it at load for every Woody, Metallic and Stony stuff,
    // modded ones too (as the game makes its carpets: DefGenerator, before blueprints and frames are made for them).
    // Each slab: label "<stuff> upper floor", colour and icon colour the stuff's, 4 of the stuff, work 300 scaled by the
    // stuff like a wall's. THE test for "is this a slab" is Is(): no code compares against PZ_UpperFloor itself.
    public static class SlabDefs
    {
        public const int Cost = 4;
        public const float BaseWork = 300f;

        private static readonly HashSet<TerrainDef> slabs = new HashSet<TerrainDef>();
        private static readonly Dictionary<ThingDef, TerrainDef> byStuff = new Dictionary<ThingDef, TerrainDef>();
        private static readonly Dictionary<TerrainDef, ThingDef> stuffOf = new Dictionary<TerrainDef, ThingDef>();

        public static IEnumerable<TerrainDef> All => slabs;

        // Any slab def (bare slab terrain, of any stuff).
        public static bool Is(TerrainDef t) => t != null && slabs.Contains(t);

        // A slab def as a buildable (blueprints', frames' entityDefToBuild, placements).
        public static bool Is(BuildableDef d) => d is TerrainDef t && slabs.Contains(t);

        // The slab of a stuff (the wood slab when the stuff has none).
        public static TerrainDef For(ThingDef stuff) => stuff != null && byStuff.TryGetValue(stuff, out TerrainDef t) ? t : PZDefOf.PZ_UpperFloor;

        public static ThingDef StuffOf(TerrainDef t) => t != null && stuffOf.TryGetValue(t, out ThingDef s) ? s : null;

        private static bool SlabStuff(ThingDef d)
        {
            if (d.stuffProps?.categories == null)
            {
                return false;
            }
            foreach (StuffCategoryDef c in d.stuffProps.categories)
            {
                if (c != null && (c.defName == "Woody" || c.defName == "Metallic" || c.defName == "Stony"))
                {
                    return true;
                }
            }
            return false;
        }

        // Called before the game makes its implied defs (blueprints and frames come after, for these too).
        public static void Generate(bool hotReload)
        {
            slabs.Clear();
            byStuff.Clear();
            stuffOf.Clear();
            TerrainDef wood = DefDatabase<TerrainDef>.GetNamedSilentFail("PZ_UpperFloor");
            ThingDef log = DefDatabase<ThingDef>.GetNamedSilentFail("WoodLog");
            if (wood == null)
            {
                return;
            }
            string baseLabel = wood.designatorDropdown?.label ?? wood.label;
            List<TerrainDef> made = new List<TerrainDef>();
            if (log != null)
            {
                Fill(wood, wood, log, baseLabel);
            }
            else
            {
                slabs.Add(wood);
            }
            foreach (ThingDef stuff in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (stuff == log || !SlabStuff(stuff))
                {
                    continue;
                }
                string defName = "PZ_UpperFloor_" + stuff.defName;
                TerrainDef t = hotReload ? DefDatabase<TerrainDef>.GetNamedSilentFail(defName) ?? new TerrainDef() : new TerrainDef();
                t.defName = defName;
                // (a def's hash and equality are its name's hash: set it before the def goes into the lookups)
                t.ResolveDefNameHash();
                CopyFrom(t, wood);
                Fill(t, wood, stuff, baseLabel);
                made.Add(t);
            }
            foreach (TerrainDef t in made)
            {
                DefGenerator.AddImpliedDef(t, hotReload);
            }
            ProjectZ.DebugLog($"slab defs: {slabs.Count} ({made.Count} made from {wood.defName})");
        }

        // Everything a slab is, from the wood slab's XML.
        private static void CopyFrom(TerrainDef t, TerrainDef w)
        {
            t.description = w.description;
            t.texturePath = w.texturePath;
            t.uiIconPath = w.uiIconPath;
            t.cropIcon = w.cropIcon;
            t.designatorDropdown = w.designatorDropdown;
            t.edgeType = w.edgeType;
            t.renderPrecedence = w.renderPrecedence;
            t.layerable = w.layerable;
            t.affordances = new List<TerrainAffordanceDef>(w.affordances);
            t.tags = w.tags == null ? null : new List<string>(w.tags);
            t.designationCategory = w.designationCategory;
            t.drawStyleCategory = w.drawStyleCategory;
            t.terrainAffordanceNeeded = w.terrainAffordanceNeeded;
            t.placeWorkers = w.placeWorkers == null ? null : new List<System.Type>(w.placeWorkers);
            t.pathCost = w.pathCost;
            t.fertility = w.fertility;
            t.canEverTerraform = w.canEverTerraform;
            t.supportsRock = w.supportsRock;
            t.preventCraters = w.preventCraters;
            t.filthAcceptanceMask = w.filthAcceptanceMask;
            t.uiOrder = w.uiOrder;
            t.modContentPack = w.modContentPack;
            t.fileName = w.fileName;
        }

        // What depends on the stuff: label, colour, icon colour, cost, work, build effect.
        private static void Fill(TerrainDef t, TerrainDef wood, ThingDef stuff, string baseLabel)
        {
            t.label = "ThingMadeOfStuffLabel".Translate(stuff.LabelAsStuff, baseLabel);
            t.color = stuff.stuffProps.color;
            t.uiIconColor = stuff.stuffProps.color;
            t.costList = new List<ThingDefCountClass> { new ThingDefCountClass(stuff, Cost) };
            t.constructEffect = stuff.stuffProps.constructEffect ?? wood.constructEffect;
            // (as the game's stat worker scales a wall's work by its stuff: factor, then offset)
            float work = BaseWork * stuff.stuffProps.statFactors.GetStatFactorFromList(StatDefOf.WorkToBuild) + stuff.stuffProps.statOffsets.GetStatOffsetFromList(StatDefOf.WorkToBuild);
            List<StatModifier> stats = new List<StatModifier>();
            foreach (StatModifier m in wood.statBases)
            {
                if (m.stat != StatDefOf.WorkToBuild)
                {
                    stats.Add(new StatModifier { stat = m.stat, value = m.value });
                }
            }
            stats.Add(new StatModifier { stat = StatDefOf.WorkToBuild, value = UnityEngine.Mathf.Max(1f, UnityEngine.Mathf.Round(work)) });
            t.statBases = stats;
            slabs.Add(t);
            byStuff[stuff] = t;
            stuffOf[t] = stuff;
        }
    }

    [HarmonyPatch(typeof(DefGenerator), nameof(DefGenerator.GenerateImpliedDefs_PreResolve))]
    public static class GenerateImpliedDefs_Slabs_Patch
    {
        public static void Prefix(bool hotReload)
        {
            SlabDefs.Generate(hotReload);
        }
    }
}

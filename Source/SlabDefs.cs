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
    // stuff like a wall's. THE test for "is this a slab" is Is(): no code compares against PZ_UpperFloor itself. Every stone
    // and metal slab also has a damaged slab (SlabDamage: after a heavy hit; cracked texture, not buildable, nothing laid on
    // it), which Is() counts as a slab too.
    public static class SlabDefs
    {
        public const int Cost = 4;
        public const float BaseWork = 300f;

        // Armoured slabs (user 2026-10-25): a slab of a Metallic stuff whose MaxHitPoints stat factor is at least
        // ArmoredHpFactor takes a heavy hit only from ArmoredBombThreshold damage, so frag grenades and mortar shells (50)
        // leave it alone (vanilla: plasteel 2.8, uranium 2.5; steel 1.0). Its damaged slab keeps the wood slab's 35.
        public const float ArmoredHpFactor = 2f;
        public const float ArmoredBombThreshold = 55f;

        private static readonly HashSet<TerrainDef> slabs = new HashSet<TerrainDef>();
        private static readonly HashSet<TerrainDef> damaged = new HashSet<TerrainDef>();
        private static readonly Dictionary<TerrainDef, TerrainDef> damagedOf = new Dictionary<TerrainDef, TerrainDef>();
        private static readonly Dictionary<ThingDef, TerrainDef> byStuff = new Dictionary<ThingDef, TerrainDef>();
        private static readonly Dictionary<TerrainDef, ThingDef> stuffOf = new Dictionary<TerrainDef, ThingDef>();

        // The buildable slabs (one per stuff; damaged slabs are not built).
        public static IEnumerable<TerrainDef> All => slabs;

        // Any slab def (bare slab terrain, of any stuff, damaged or not).
        public static bool Is(TerrainDef t) => t != null && (slabs.Contains(t) || damaged.Contains(t));

        // A slab def as a buildable (blueprints', frames' entityDefToBuild, placements).
        public static bool Is(BuildableDef d) => d is TerrainDef t && Is(t);

        // A damaged slab (SlabDamage: a stone or metal slab after one heavy hit).
        public static bool IsDamaged(TerrainDef t) => t != null && damaged.Contains(t);

        // What a slab becomes after a heavy hit: its damaged def; null for wood slabs (they break) and damaged ones.
        public static TerrainDef DamagedOf(TerrainDef t) => t != null && damagedOf.TryGetValue(t, out TerrainDef d) ? d : null;

        public static IEnumerable<TerrainDef> AllDamaged => damaged;

        // The slab of a stuff (the wood slab when the stuff has none).
        public static TerrainDef For(ThingDef stuff) => stuff != null && byStuff.TryGetValue(stuff, out TerrainDef t) ? t : PZDefOf.PZ_UpperFloor;

        public static ThingDef StuffOf(TerrainDef t) => t != null && stuffOf.TryGetValue(t, out ThingDef s) ? s : null;

        private static bool Woody(ThingDef d)
        {
            if (d?.stuffProps?.categories == null)
            {
                return false;
            }
            foreach (StuffCategoryDef c in d.stuffProps.categories)
            {
                if (c != null && c.defName == "Woody")
                {
                    return true;
                }
            }
            return false;
        }

        public static bool Armored(ThingDef d)
        {
            if (d?.stuffProps?.categories == null || d.stuffProps.statFactors.GetStatFactorFromList(StatDefOf.MaxHitPoints) < ArmoredHpFactor)
            {
                return false;
            }
            foreach (StuffCategoryDef c in d.stuffProps.categories)
            {
                if (c != null && c.defName == "Metallic")
                {
                    return true;
                }
            }
            return false;
        }

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
            damaged.Clear();
            damagedOf.Clear();
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
            // The damaged slab of every stone and metal slab (wood slabs break at once): not built, nothing laid on it
            // (changeable false, the game's own "no floor here"), taken up with the remove tool like any slab.
            List<TerrainDef> broken = new List<TerrainDef>();
            foreach (TerrainDef s in new List<TerrainDef>(slabs))
            {
                ThingDef stuff = StuffOf(s);
                if (stuff == null || Woody(stuff))
                {
                    continue;
                }
                string defName = "PZ_UpperFloorDamaged_" + stuff.defName;
                TerrainDef d = hotReload ? DefDatabase<TerrainDef>.GetNamedSilentFail(defName) ?? new TerrainDef() : new TerrainDef();
                d.defName = defName;
                d.ResolveDefNameHash();
                CopyFrom(d, wood);
                d.texturePath = DamagedTexture;
                d.designatorDropdown = null;
                d.designationCategory = null;
                d.placeWorkers = null;
                d.changeable = false;
                d.label = "PZ_DamagedSlab".Translate(s.label);
                d.description = "PZ_DamagedSlabDesc".Translate();
                d.color = s.color;
                d.uiIconColor = s.uiIconColor;
                d.costList = s.costList == null ? null : new List<ThingDefCountClass>(s.costList);
                d.constructEffect = s.constructEffect;
                d.statBases = new List<StatModifier>();
                foreach (StatModifier m in s.statBases)
                {
                    d.statBases.Add(new StatModifier { stat = m.stat, value = m.value });
                }
                damaged.Add(d);
                damagedOf[s] = d;
                stuffOf[d] = stuff;
                broken.Add(d);
            }
            foreach (TerrainDef t in made)
            {
                DefGenerator.AddImpliedDef(t, hotReload);
            }
            foreach (TerrainDef t in broken)
            {
                DefGenerator.AddImpliedDef(t, hotReload);
            }
            ProjectZ.DebugLog($"slab defs: {slabs.Count} ({made.Count} made from {wood.defName}), {broken.Count} damaged");
        }

        public const string DamagedTexture = "ProjectZ/Terrain/SlabLatticeDamaged";

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
            t.destroyOnBombDamageThreshold = w.destroyOnBombDamageThreshold;
            t.modContentPack = w.modContentPack;
            t.fileName = w.fileName;
        }

        // What depends on the stuff: label, colour, icon colour, cost, work, build effect, heavy-hit threshold (Armored).
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
            if (Armored(stuff))
            {
                t.destroyOnBombDamageThreshold = ArmoredBombThreshold;
                t.description = wood.description + "\n\n" + "PZ_ArmoredSlabDesc".Translate();
            }
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

using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace ProjectZ
{
    // Api 3: one kind of vertical link (one net kind) for ProjectZ to draw like power risers (Api.AddRiserSource). Subclass
    // it in the addon, add one instance once, and call MarkDirty(map) whenever the links of a map may have changed (a
    // transmitter spawned, despawned or switched): ProjectZ asks Collect again only then, not every frame.
    public abstract class RiserSource
    {
        // Fill 'into' with this map's links (only on a map with floors; asked again only after MarkDirty). A link is named by
        // its lower cell; ProjectZ takes the upper one as Api.StackedCell(low, map, true) and drops a link without one.
        public abstract void Collect(Map map, List<RiserLink> into);

        // Asked each frame while drawing (the current map): is this source's overlay shown now? Only then are the links'
        // overlay lines drawn.
        public virtual bool OverlayShown(Map map) => false;

        // This source's links on the map are stale.
        public void MarkDirty(Map map) => RiserView.MarkDirty(map, this);

        // What the last frame that drew drew for this source: solid pieces, overlay links, the frame (tests).
        public int LastPieces { get; internal set; }

        public int LastLinks { get; internal set; }

        public int LastFrame { get; internal set; } = -1;
    }

    // One vertical link, between the lower cell and the cell stacked over it.
    public struct RiserLink
    {
        public IntVec3 low;               // the lower cell
        public Color32 colour;            // the solid piece's colour
        public float width;               // the piece's width in cells (0: ProjectZ's default, 0.22, as the conduit risers)
        public bool lowDrawn, highDrawn;  // that end is visible (false for a thing the game draws invisible: no piece there)
        public bool overlay;              // drawn in the overlay (e.g. the player's or nobody's, as the game's overlays)
        public Material overlayLine;      // the upright overlay line (null: no overlay link); a tile with v along the link
        public Material overlayEnd;       // the mark at each end, flat on the floor (null: none)
    }
}

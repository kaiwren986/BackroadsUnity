// Site props, fences and lane A's stand-ins as baked for a scene (card sites-S4 step 4, design §7).
using System;
using UnityEngine;

namespace Backroads.Sites
{
    /// <summary>
    /// card sites-S4 step 4: every prop a town's site plans place (design §7's table: a Synty prefab by path, or one of our
    /// own simple stand-ins where the packs have nothing), with its height on the graded ground, yaw, parcel and the rule
    /// that placed it; the fence runs and gates (Synty fence pieces along them in the scene; box colliders at run time); and
    /// the boxes standing in for lane A's site models (canopies, dispensers, pylons, grain bins, ...) until lane A models
    /// them. Baked by Backroads.Sites.Editor.SiteBake; placed in a scene by SitePropPlacer (Backroads.Sites.Client.Editor).
    /// Synty files are never edited or merged: placements name the prefab only (AGENTS.md rule 3).
    /// </summary>
    public sealed class SitePropSet : ScriptableObject
    {
        public const int FormatVersion = 1;
        public const string ResourceName = "BrSiteProps";

        public static string TownResourceName(string townId) => ResourceName + "_" + townId;
        public static SitePropSet LoadTown(string townId) => Resources.Load<SitePropSet>(TownResourceName(townId));

        [Serializable]
        public struct Placement
        {
            /// <summary>The prop table's key (design §7), e.g. "lamp", "sign.parking", "dumpster".</summary>
            public string key;
            /// <summary>Asset paths of the Synty prefabs, bottom to top (a lamp's pole, then its head on top); empty: our own stand-in.</summary>
            public string[] prefabs;
            public Vector3 position;
            /// <summary>Degrees about +Y; 0 faces +Z (Unity's yaw).</summary>
            public float yaw;
            public int parcel;
            public string reason;
        }

        [Serializable]
        public struct FenceRun
        {
            public Vector3[] line;
            public FenceKind kind;
            public float height;
            public int parcel;
        }

        [Serializable]
        public struct GateSpan
        {
            public Vector3 a, b;
            public FenceKind kind;
            public bool sliding;
            public int parcel;
        }

        /// <summary>A lane A site model's stand-in (ModelRequest): its footprint (x, z), base height and height.</summary>
        [Serializable]
        public struct ModelBox
        {
            public string key;
            public Vector2[] footprint;
            public float y, height;
            public int parcel;
            public string reason;
        }

        public int formatVersion = FormatVersion;
        public int bakeVersion;
        public string source = "", plansHash = "";
        /// <summary>SHA-256 of the placements, fences, gates and boxes (mm and 0.01 degree).</summary>
        public string contentHash = "";
        public Placement[] props = Array.Empty<Placement>();
        public FenceRun[] fences = Array.Empty<FenceRun>();
        public GateSpan[] gates = Array.Empty<GateSpan>();
        public ModelBox[] models = Array.Empty<ModelBox>();
    }
}

// Site plans (card sites-S3, design docs/design/site-layouts.md §2.2, §4, §6, §7, §7a).
using System;
using System.Collections.Generic;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Sites
{
    public enum PlanSource : byte { Mapped = 0, Template = 1, Rules = 2 }

    /// <summary>What a surface polygon is for (S4 materials, grading limits, the profile check).</summary>
    public enum SurfaceUse : byte { Lot = 0, Drive = 1, Apron = 2, Forecourt = 3, Walk = 4, Yard = 5, Pad = 6 }

    [Serializable]
    public sealed class SurfacePoly
    {
        public double2[] outer;
        public double2[][] holes = Array.Empty<double2[]>();
        public SiteSurface surface;
        public SurfaceUse use;
        /// <summary>0 = fresh, 1 = worn (lot asphalt is lighter than the road's).</summary>
        public float wear;
    }

    public enum FenceKind : byte { ChainLink = 0, Wood = 1, Picket = 2, Farm = 3 }

    [Serializable]
    public sealed class Fence
    {
        public double2[] line;
        public FenceKind kind;
        public float height;
    }

    [Serializable]
    public struct Gate
    {
        public double2 a, b;
        public FenceKind kind;
        public bool sliding;
    }

    /// <summary>A prop placement by key (design §7's table; S4 maps keys to Synty prefabs or our own meshes).</summary>
    [Serializable]
    public struct PropSpot
    {
        public string key;
        public double2 pos;
        public float yaw;
        public string reason;
    }

    /// <summary>Something lane A models for the site (canopy, dispensers, pylon, grain bins, scale house), design §7a.</summary>
    [Serializable]
    public struct ModelRequest
    {
        public string key;
        public double2[] footprint;
        public float height;
        public string reason;
    }

    [Serializable]
    public sealed class SitePlan
    {
        public int parcel;
        public BusinessType type;
        public bool downtown;
        public PlanSource source;
        public string templateId;
        public bool mirrored;
        public double2 scale = new double2(1, 1);
        public readonly List<SurfacePoly> surfaces = new List<SurfacePoly>();
        public readonly List<LotLayout> lots = new List<LotLayout>();
        public readonly List<DrivewayRequest> driveways = new List<DrivewayRequest>();
        public readonly List<DrivewayCut> cuts = new List<DrivewayCut>();
        public readonly List<Fence> fences = new List<Fence>();
        public readonly List<Gate> gates = new List<Gate>();
        public readonly List<PropSpot> props = new List<PropSpot>();
        public readonly List<DoorSpec> doors = new List<DoorSpec>();
        public readonly List<ModelRequest> models = new List<ModelRequest>();
        /// <summary>Fast food: the drive-through lane round the building (path, window, order board, stacking).</summary>
        public DriveThroughLane driveThrough;
        public string[] osmCredit = Array.Empty<string>();
        public readonly List<string> notes = new List<string>();
    }
}

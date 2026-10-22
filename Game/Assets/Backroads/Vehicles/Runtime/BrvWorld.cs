using System;
using Backroads.World;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Backroads.Vehicles
{
    /// <summary>One browser-game world.js rut() call as BRV emits it (JS world coordinates), replayed into IStampSink.</summary>
    public struct RutCall
    {
        public float x, z, radius, depth, rate, wearAdd, berm, fx, fz;
        /// <summary>Fleet index of the vehicle that made it.</summary>
        public int vehicle;
    }

    /// <summary>A tree a vehicle knocked over (props.js knockTree): VegetationSet index and the fall direction (JS space).</summary>
    public struct TreeKnock
    {
        public int collider, tree;
        public float dirX, dirZ;
        public int vehicle;
    }

    /// <summary>
    /// What the BRV step reads from the world (Burst-friendly): the WorldGrid layers (heights are read through the
    /// double-precision JS-mirror queries, WorldGridQueriesRef) and the static colliders of props.js (ColliderGrid's
    /// CSR arrays), plus the per-collider tree state the vehicles change (knocked-over trees stop colliding).
    /// </summary>
    public struct BrvWorld
    {
        public WorldGridView grid;
        [ReadOnly] public NativeArray<ColliderGrid.Record> colliders;
        [ReadOnly] public NativeArray<int> cellStart;
        [ReadOnly] public NativeArray<int> cellItems;
        /// <summary>Per collider: 9000 * s^2 * strength for standing trees (the knock-over momentum), else 0.</summary>
        [ReadOnly] public NativeArray<double> treeKnockMomentum;
        /// <summary>Per collider: 1 once knocked over (props.js sets c.r = 0, c.y1 = c.y0).</summary>
        [NativeDisableContainerSafetyRestriction] public NativeArray<byte> treeDown;
        /// <summary>16 m collider cells per side (ColliderGrid.CellsPerSide: 64 on canon v1, 128 on Millbrook).</summary>
        public int colliderCells;
        /// <summary>
        /// The PhysX chassis resolves static and vehicle-vehicle contacts (T3.6): BRV skips the browser game's 2D statics
        /// (except tree knock-over) and the vehicle pairs. Set by BrvFleet.PhysxCollisions.
        /// </summary>
        public bool physx;

        public bool HasColliders => colliders.IsCreated && colliders.Length > 0;
    }

    /// <summary>Owns the per-collider arrays of a <see cref="BrvWorld"/> built from the canon.</summary>
    public sealed class BrvWorldData : IDisposable
    {
        public NativeArray<double> TreeKnockMomentum;
        public NativeArray<byte> TreeDown;
        // empty collider arrays for a world without static colliders (the BRV test track): jobs need valid containers
        NativeArray<ColliderGrid.Record> _noRecords;
        NativeArray<int> _noCells;
        public WorldGrid Grid { get; private set; }
        public ColliderGrid Colliders { get; private set; }

        public static BrvWorldData Create(WorldGrid grid, ColliderGrid colliders, VegetationSet vegetation)
        {
            var d = new BrvWorldData { Grid = grid, Colliders = colliders };
            int n = colliders != null ? colliders.Count : 0;
            if (colliders == null)
            {
                d._noRecords = new NativeArray<ColliderGrid.Record>(0, Allocator.Persistent);
                d._noCells = new NativeArray<int>(0, Allocator.Persistent);
            }
            d.TreeKnockMomentum = new NativeArray<double>(n, Allocator.Persistent);
            d.TreeDown = new NativeArray<byte>(n, Allocator.Persistent);
            int treeKind = ColliderGrid.KindIndex("tree");
            for (int i = 0; i < n; i++)
            {
                var c = colliders.Records[i];
                if (c.type != 0 || c.kind != treeKind || c.tree < 0 || vegetation == null || c.tree >= vegetation.trees.Length) continue;
                var t = vegetation.trees[c.tree];
                double strength = t.sp >= 0 && t.sp < vegetation.species.Length ? vegetation.species[t.sp].strength : 1.0;
                d.TreeKnockMomentum[i] = 9000.0 * t.s * t.s * strength;
            }
            return d;
        }

        public BrvWorld View => new BrvWorld
        {
            grid = Grid.View,
            colliders = Colliders != null ? Colliders.Records : _noRecords,
            cellStart = Colliders != null ? Colliders.CellStart : _noCells,
            cellItems = Colliders != null ? Colliders.CellItems : _noCells,
            treeKnockMomentum = TreeKnockMomentum,
            treeDown = TreeDown,
            colliderCells = Colliders != null ? Colliders.CellsPerSide : Grid.Layout.ColliderCellsPerSide,
        };

        public void Dispose()
        {
            if (TreeKnockMomentum.IsCreated) TreeKnockMomentum.Dispose();
            if (TreeDown.IsCreated) TreeDown.Dispose();
            if (_noRecords.IsCreated) _noRecords.Dispose();
            if (_noCells.IsCreated) _noCells.Dispose();
        }
    }
}

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Backroads.Vehicles
{
    /// <summary>
    /// One fixed step (1/120 s) of every BRV V0 vehicle in a fleet, in the browser game's loop order (main.js update):
    /// the player's controls (vehicleControls), then step() for each vehicle in fleet order, then collideVehicles for
    /// every pair. With <see cref="Only"/> &gt;= 0 it steps that one vehicle and nothing else (settle()).
    /// Rut calls and knocked trees are recorded in step order for the caller to replay on the main thread.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public unsafe struct VehicleStepJob : IJob
    {
        [NativeDisableContainerSafetyRestriction] public NativeArray<BrvState> States;
        [ReadOnly] public NativeArray<BrvDef> Defs;
        [ReadOnly] public NativeArray<BrvInput> Inputs;
        public NativeArray<BrvControlEvents> Events;
        public BrvWorld World;
        public NativeList<RutCall> Ruts;
        public NativeList<TreeKnock> Knocks;
        public int Count;
        public int Only;
        public double Dt;

        public void Execute()
        {
            var sp = States.GetUnsafePtr();
            var dp = Defs.GetUnsafeReadOnlyPtr();
            if (Only >= 0)
            {
                ref var s = ref UnsafeUtility.ArrayElementAsRef<BrvState>(sp, Only);
                ref var d = ref UnsafeUtility.ArrayElementAsRef<BrvDef>(dp, Only);
                BrvSim.Step(ref s, d, World, Dt, Only, ref Ruts, ref Knocks, States, Count);
                return;
            }
            for (int i = 0; i < Count; i++)
            {
                if (!Inputs[i].active) continue;
                ref var s = ref UnsafeUtility.ArrayElementAsRef<BrvState>(sp, i);
                ref var d = ref UnsafeUtility.ArrayElementAsRef<BrvDef>(dp, i);
                var ev = Events[i];
                BrvSim.Controls(ref s, d, World, Inputs[i], Dt, i, States, Defs, Count, ref ev);
                Events[i] = ev;
            }
            for (int i = 0; i < Count; i++)
            {
                ref var s = ref UnsafeUtility.ArrayElementAsRef<BrvState>(sp, i);
                ref var d = ref UnsafeUtility.ArrayElementAsRef<BrvDef>(dp, i);
                BrvSim.Step(ref s, d, World, Dt, i, ref Ruts, ref Knocks, States, Count);
            }
            for (int i = 0; i < Count; i++)
                for (int j = i + 1; j < Count; j++)
                {
                    ref var a = ref UnsafeUtility.ArrayElementAsRef<BrvState>(sp, i);
                    ref var b = ref UnsafeUtility.ArrayElementAsRef<BrvState>(sp, j);
                    ref var da = ref UnsafeUtility.ArrayElementAsRef<BrvDef>(dp, i);
                    ref var db = ref UnsafeUtility.ArrayElementAsRef<BrvDef>(dp, j);
                    BrvSim.CollideVehicles(ref a, da, ref b, db);
                }
        }
    }
}

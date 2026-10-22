using UnityEngine;

namespace Backroads.Vehicles
{
    /// <summary>Marks a vehicle's PhysX chassis body (index into its fleet). Editor validation looks for it (T3.6).</summary>
    [DisallowMultipleComponent]
    public sealed class BrvChassisBody : MonoBehaviour
    {
        public int Index;
    }
}

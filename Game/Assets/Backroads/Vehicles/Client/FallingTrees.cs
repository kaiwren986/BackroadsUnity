using System.Collections.Generic;
using Backroads.World;
using UnityEngine;

namespace Backroads.Vehicles.Client
{
    /// <summary>
    /// Trees a vehicle knocks over fall the way props.js shows them (updateFallingTrees: fall += dt (0.6 + 2.5 fall) up
    /// to 1, then Euler(fall^2 * 1.45, fallYaw, 0, 'YXZ') about the stump). The vehicle model already stopped colliding
    /// with the tree (BrvWorldData.TreeDown); this only moves lane W's tree object ("Vegetation/&lt;species&gt;/t&lt;index&gt;",
    /// VegetationRenderer). Until W's vegetation offers an API for it, the object is found by name; if it is not there
    /// nothing happens.
    /// </summary>
    public sealed class FallingTrees : MonoBehaviour
    {
        sealed class Falling { public Transform t; public float fall, yawJs; public Vector3 pos; }

        readonly List<Falling> _falling = new List<Falling>();
        Transform _vegetation;

        public int Knocked { get; private set; }

        public void Knock(TreeKnock k)
        {
            Knocked++;
            // the world the vehicles drive in (T3.6), else the canon
            var veg = ActiveWorld.IsSet ? ActiveWorld.Vegetation : CanonRuntime.Asset != null ? CanonRuntime.Asset.vegetation : null;
            if (veg == null || k.tree < 0 || k.tree >= veg.trees.Length) return;
            if (_vegetation == null) { var go = GameObject.Find("Vegetation"); if (go != null) _vegetation = go.transform; }
            if (_vegetation == null) return;
            var tree = veg.trees[k.tree];
            var sp = _vegetation.Find(veg.species[tree.sp].name);
            var t = sp != null ? sp.Find("t" + k.tree) : null;
            if (t == null) return;
            _falling.Add(new Falling { t = t, fall = 0, yawJs = Mathf.Atan2(k.dirX, k.dirZ), pos = t.localPosition });
        }

        void Update()
        {
            float dt = Time.deltaTime;
            for (int i = _falling.Count - 1; i >= 0; i--)
            {
                var f = _falling[i];
                if (f.t == null) { _falling.RemoveAt(i); continue; }
                f.fall = Mathf.Min(1f, f.fall + dt * (0.6f + f.fall * 2.5f));
                float ang = f.fall * f.fall * 1.45f;
                // JS Euler(ang, yaw, 0, 'YXZ') = Ry(yaw) Rx(ang); x flipped: Ry(-yaw) Rx(ang)
                f.t.localRotation = Quaternion.AngleAxis(-f.yawJs * Mathf.Rad2Deg, Vector3.up) * Quaternion.AngleAxis(ang * Mathf.Rad2Deg, Vector3.right);
                f.t.localPosition = f.pos;
                if (f.fall >= 1f) _falling.RemoveAt(i);
            }
        }
    }
}

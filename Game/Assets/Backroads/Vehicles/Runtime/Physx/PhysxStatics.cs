using System;
using Backroads.World;
using UnityEngine;

namespace Backroads.Vehicles
{
    /// <summary>
    /// The browser game's static colliders (props.js, ColliderGrid: 4,013 on canon v1) as PhysX statics on layer Static
    /// (card T3.6): circles become upright capsules (radius r, the straight part from y0 to y1), boxes become boxes
    /// (2 hw x (y1 - y0) x 2 hd, turned by rot). Index i is collider i of the grid, so a knocked-over tree turns its
    /// capsule off (props.js sets the tree's r to 0). Shared code: the server builds them too.
    /// </summary>
    public sealed class PhysxStatics : IDisposable
    {
        readonly GameObject _root;
        readonly GameObject[] _items;

        public int Count => _items.Length;
        public int Boxes { get; private set; }
        public int Capsules { get; private set; }
        public Transform Root => _root.transform;

        PhysxStatics(GameObject root, int n)
        {
            _root = root;
            _items = new GameObject[n];
        }

        public static PhysxStatics Build(ColliderGrid colliders, Transform parent)
        {
            if (colliders == null) throw new ArgumentNullException(nameof(colliders));
            int n = colliders.Count;
            var root = new GameObject("StaticColliders");
            if (parent != null) root.transform.SetParent(parent, false);
            var st = new PhysxStatics(root, n);
            int layer = BrvPhysxChassis.Layer(BrvPhysxChassis.LayerStatic);
            root.layer = layer;
            for (int i = 0; i < n; i++)
            {
                var c = colliders.Records[i];
                float h = Mathf.Max(0.05f, c.y1 - c.y0);
                var p = ColliderGrid.UnityCentre(c);
                var go = new GameObject($"{ColliderGrid.Kinds[c.kind]} {i}") { layer = layer };
                go.transform.SetParent(root.transform, false);
                go.transform.SetPositionAndRotation(new Vector3(p.x, p.y, p.z), Quaternion.Euler(0f, c.type == 1 ? ColliderGrid.UnityYawDeg(c) : 0f, 0f));
                if (c.type == 1)
                {
                    var b = go.AddComponent<BoxCollider>();
                    b.size = new Vector3(2 * c.hw, h, 2 * c.hd);
                    st.Boxes++;
                }
                else
                {
                    var cap = go.AddComponent<CapsuleCollider>();
                    cap.direction = 1;
                    cap.radius = Mathf.Max(0.01f, c.r);
                    cap.height = h + 2 * cap.radius;
                    st.Capsules++;
                }
                go.isStatic = true;
                st._items[i] = go;
            }
            return st;
        }

        public Collider ColliderOf(int i) => i >= 0 && i < _items.Length && _items[i] != null ? _items[i].GetComponent<Collider>() : null;

        /// <summary>A vehicle knocked tree collider i over: it stops colliding.</summary>
        public void Knock(int i)
        {
            if (i >= 0 && i < _items.Length && _items[i] != null) _items[i].SetActive(false);
        }

        public void Dispose()
        {
            if (_root != null) BrvPhysxChassis.DestroyNow(_root);
        }
    }
}

using UnityEditor;
using UnityEngine;

namespace Backroads.Roads.Client.Editor
{
    /// <summary>
    /// The editor gizmo view of a RoadNetworkView (T2.8 step 3): nodes (sphere coloured by kind; magenta ring = needs a
    /// decision), arms (cyan major, red stop, white other, out to the mouth), fillet tangent points (magenta), provisional
    /// mouths (yellow, orange when the arm is too short) and kerb arcs (white), centrelines by class, and labels with the
    /// node number, kind and control.
    /// </summary>
    static class RoadNetworkGizmos
    {
        [DrawGizmo(GizmoType.Selected | GizmoType.NonSelected | GizmoType.Pickable)]
        static void Draw(RoadNetworkView view, GizmoType type)
        {
            var a = view.network;
            if (a == null) return;
            var up = Vector3.up * view.lift;
            if (view.centrelines)
                foreach (var e in a.edges)
                {
                    Gizmos.color = PlainRibbonMesh.ClassColor(e.cls);
                    for (int i = 0; i + 1 < e.points.Length; i++) Gizmos.DrawLine(e.points[i] + up, e.points[i + 1] + up);
                }
            foreach (var n in a.nodes)
            {
                var p = n.position + up;
                var np = new Vector2(n.position.x, n.position.z);
                Gizmos.color = RoadGraphOverlay.KindColor(n.kind, n.armCount);
                Gizmos.DrawSphere(p, 1.6f);
                if ((n.flags & NodeFlags.ComplexPair) != 0)
                {
                    Handles.color = RoadGraphOverlay.DecisionColor;
                    Handles.DrawWireDisc(p, Vector3.up, 5f);
                }
                for (int k = 0; k < n.armCount; k++)
                {
                    var arm = a.arms[n.firstArm + k];
                    bool major = k == n.majorA || k == n.majorB;
                    Gizmos.color = major ? (Color)RoadGraphOverlay.MajorColor : RoadGraphOverlay.IsStopArm(n, k) ? (Color)RoadGraphOverlay.StopColor : Color.white;
                    var dir = new Vector3(arm.dir.x, 0, arm.dir.y);
                    float len = Mathf.Min(arm.length, Mathf.Max(10f, arm.mouth));
                    Gizmos.DrawLine(p, p + dir * len);
                    if (arm.mouth > 0 && arm.mouth < arm.length)
                    {
                        var side = new Vector3(-arm.dir.y, 0, arm.dir.x) * arm.halfWidth;
                        Gizmos.color = arm.shortArm ? (Color)RoadGraphOverlay.ShortMouthColor : RoadGraphOverlay.MouthColor;
                        var m = p + dir * arm.mouth;
                        Gizmos.DrawLine(m - side, m + side);
                    }
                }
                if (view.corners)
                    for (int k = 0; k < n.cornerCount; k++)
                    {
                        var c = a.corners[n.firstCorner + k];
                        if (c.kind != CornerKind.Fillet) continue;
                        var I = a.arms[c.armI];
                        var J = a.arms[c.armJ];
                        Vector2 ti = np + I.dir * c.tI + new Vector2(-I.dir.y, I.dir.x) * I.halfWidth;
                        Vector2 tj = np + J.dir * c.tJ + new Vector2(J.dir.y, -J.dir.x) * J.halfWidth;
                        var arc = RoadGraphOverlay.Arc(c.centre, c.radius, ti, tj, np);
                        var pts = new Vector3[arc.Count];
                        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector3(arc[i].x, p.y, arc[i].y);
                        Handles.color = Color.white;
                        Handles.DrawPolyLine(pts);
                        Gizmos.color = RoadGraphOverlay.TangentColor;
                        Gizmos.DrawSphere(new Vector3(ti.x, p.y, ti.y), 0.45f);
                        Gizmos.DrawSphere(new Vector3(tj.x, p.y, tj.y), 0.45f);
                    }
                if (view.labels)
                {
                    string major = n.HasMajor ? $" major R{a.arms[n.firstArm + n.majorA].sourceRoad}" : "";
                    Handles.Label(p + Vector3.up * 3f, $"{n.id} {n.kind} {n.control}{major}" + (n.legacyId < 0 ? " (new)" : ""));
                }
            }
        }
    }
}

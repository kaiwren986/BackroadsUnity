using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Roads;
using Backroads.Roads.Editor;
using Backroads.World;
using NUnit.Framework;
using UnityEngine;

namespace Backroads.Tests.EditMode.Roads
{
    /// <summary>
    /// The cameras of m3.shots.junctions (JunctionShots) on the committed town network: every one of the eight places is
    /// found, each on its own node, inside the play square, 10-60 m from what it looks at (the bridge view 100 m), deterministic, and no
    /// building of the map stands between a camera and its junction.
    /// </summary>
    [TestFixture, Category("Roads")]
    public class RoadShots
    {
        [Test]
        public void PicksEveryPlace()
        {
            string town = TownRoadSource.Towns().First();
            var net = RoadNetworkBaker.LoadBakedTown(town);
            Assert.NotNull(net, "no committed " + RoadNetworkBaker.TownAssetPath(town));
            var shots = JunctionShots.Pick(net, out var missing);
            foreach (var s in shots) TestContext.WriteLine(JunctionShots.Describe(s));
            Assert.IsEmpty(missing, "places with no candidate: " + string.Join(", ", missing));
            CollectionAssert.AreEqual(JunctionShots.Ids, shots.Select(s => s.id).ToArray());
            Assert.AreEqual(shots.Count, shots.Select(s => s.node).Distinct().Count(), "every place a different node");
            float half = WorldGridLayout.Millbrook.Half;
            foreach (var s in shots)
            {
                Assert.Less(Mathf.Max(Mathf.Abs(s.position.x), Mathf.Abs(s.position.z)), half, s.id + ": the camera is inside the play square");
                float d = Vector3.Distance(s.position, s.target);
                // the bridge view looks along the deck, 24 m before its end to 60 m onto it
                Assert.That(d, Is.InRange(10f, s.id == "bridge" ? 100f : 60f), s.id + ": distance to what it looks at");
                Assert.Greater(s.position.y, s.target.y - 10f, s.id + ": the camera is not far below its junction");
            }
            var again = JunctionShots.Pick(net, out _);
            for (int i = 0; i < shots.Count; i++)
            {
                Assert.AreEqual(shots[i].node, again[i].node);
                Assert.AreEqual(shots[i].position, again[i].position);
            }
            // the view is not blocked by a building: the camera-to-junction line crosses no footprint of the map
            var footprints = Kerbs.Get("town").k.roads.input.footprints.Where(f => f.corners != null && f.corners.Length >= 3).Select(f => f.corners).ToList();
            foreach (var s in shots)
            {
                var a = new D2(s.position.x, s.position.z);
                var b = new D2(s.target.x, s.target.z);
                var hit = footprints.FirstOrDefault(c => Crosses(a, b, c));
                Assert.IsNull(hit, $"{s.id}: a building stands between the camera and node {s.node}" + (hit != null ? $" (near {hit[0]})" : ""));
            }
        }

        static bool Crosses(D2 a, D2 b, D2[] poly)
        {
            if (TownZone.InPolygon(poly, a) || TownZone.InPolygon(poly, b)) return true;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if (Geom2.SegmentCross(a, b, poly[j], poly[i], out _, out _)) return true;
            return false;
        }
    }
}

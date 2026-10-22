using System;
using System.Collections.Generic;
using System.IO;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using UnityEngine;

namespace Backroads.World.Bake
{
    /// <summary>
    /// T3.3 follow-ups (lane S's S4 request 2): the driveway requests lane S's site plans make, from
    /// tools/world/sites/(town)/driveways.json, in the order lane R's cutter takes them. WorldBake resolves them all at once
    /// with DrivewayCutter.Resolve (the cuts the sites graded against) and hands cuts and cutter to RoadPlateBuilder.Build,
    /// which adds the apron plates.
    /// </summary>
    public static class DrivewaySource
    {
        [Serializable]
        sealed class FileJson
        {
            public string format;
            public int version;
            public string plansHash;
            public RequestJson[] requests;
        }

        [Serializable]
        sealed class RequestJson
        {
            public int id, faceId;
            public float s0, s1;
            public string kind;
            public float flareLength, returnRadius;
            public string surface;
            public bool mapped;
            public string osmWay;
        }

        public static string PathFor(string townId) => RepoPaths.InRepo("tools/world/sites/" + townId + "/driveways.json");

        /// <summary>The requests, or null when the town has no driveways file. sha256: the file's hash (for the manifest).</summary>
        public static List<DrivewayRequest> Load(string townId, out string sha256)
        {
            sha256 = null;
            var path = PathFor(townId);
            if (!File.Exists(path)) return null;
            var f = JsonUtility.FromJson<FileJson>(File.ReadAllText(path));
            if (f == null || f.format != "backroads-driveways") throw new InvalidDataException("not a backroads-driveways file: " + path);
            if (f.version != 1) throw new InvalidDataException($"driveways.json version {f.version}, expected 1: " + path);
            sha256 = TownSource.Sha256File(path);
            var list = new List<DrivewayRequest>(f.requests?.Length ?? 0);
            foreach (var r in f.requests ?? Array.Empty<RequestJson>())
                list.Add(new DrivewayRequest
                {
                    id = r.id, faceId = r.faceId, s0 = r.s0, s1 = r.s1,
                    kind = (DrivewayKind)Enum.Parse(typeof(DrivewayKind), r.kind),
                    flareLength = r.flareLength, returnRadius = r.returnRadius,
                    surface = (SiteSurface)Enum.Parse(typeof(SiteSurface), r.surface),
                    mapped = r.mapped, osmWay = string.IsNullOrEmpty(r.osmWay) ? null : r.osmWay,
                });
            return list;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Backroads.Build.Editor;
using Backroads.Core.WorldBake;
using UnityEngine;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// The driveway cuts of a town for the road meshes (lane S's sites-S4 request): lane S's site plans write their
    /// driveway requests to tools/world/sites/(town)/driveways.json in the order the cutter takes them; the bake resolves
    /// them all at once with DrivewayCutter against the town's kerb frontage, exactly as the sites graded against and as
    /// lane W's terrain bake does for the apron plates, and RoadMeshBuilder drops the kerb and the walk at every cut.
    /// The file also carries lane S's answers (outcome, face, throat, merge) for comparison.
    /// </summary>
    public static class RoadDriveways
    {
        [Serializable]
        sealed class FileJson
        {
            public string format;
            public int version;
            public string plansHash;
            public RequestJson[] requests;
            public AnswerJson[] answers;
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

        [Serializable]
        public struct AnswerJson
        {
            public int requestId;
            public string outcome;
            public int faceId;
            public float s0, s1;
            public int mergedWith;
        }

        public sealed class DrivewayFile
        {
            public string path, sha256, plansHash;
            public readonly List<DrivewayRequest> requests = new List<DrivewayRequest>();
            public readonly List<AnswerJson> answers = new List<AnswerJson>();
        }

        public static string PathFor(string townId) => RepoPaths.InRepo("tools/world/sites/" + townId + "/driveways.json");

        /// <summary>The town's driveway requests, or null when it has no driveways file.</summary>
        public static DrivewayFile Load(string townId)
        {
            var path = PathFor(townId);
            if (!System.IO.File.Exists(path)) return null;
            var bytes = System.IO.File.ReadAllBytes(path);
            var f = JsonUtility.FromJson<FileJson>(System.Text.Encoding.UTF8.GetString(bytes));
            if (f == null || f.format != "backroads-driveways") throw new InvalidDataException("not a backroads-driveways file: " + path);
            if (f.version != 1) throw new InvalidDataException($"driveways.json version {f.version}, expected 1: " + path);
            var o = new DrivewayFile { path = path, plansHash = f.plansHash ?? "" };
            using (var sha = SHA256.Create()) o.sha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            foreach (var r in f.requests ?? Array.Empty<RequestJson>())
                o.requests.Add(new DrivewayRequest
                {
                    id = r.id, faceId = r.faceId, s0 = r.s0, s1 = r.s1,
                    kind = (DrivewayKind)Enum.Parse(typeof(DrivewayKind), r.kind),
                    flareLength = r.flareLength, returnRadius = r.returnRadius,
                    surface = (SiteSurface)Enum.Parse(typeof(SiteSurface), r.surface),
                    mapped = r.mapped, osmWay = string.IsNullOrEmpty(r.osmWay) ? null : r.osmWay,
                });
            if (f.answers != null) o.answers.AddRange(f.answers);
            return o;
        }

        /// <summary>
        /// A kerb bake's driveway cuts: the town's requests resolved all at once against its frontage (null when the town has
        /// no driveways file). Fills k.cuts, k.cutter and k.drivewaysSha256.
        /// </summary>
        public static DrivewayCut[] ForBake(KerbBaker.Bake k)
        {
            k.cuts = null; k.cutter = null; k.drivewaysSha256 = "";
            if (k?.kerbs == null || string.IsNullOrEmpty(k.town)) return null;
            var file = Load(k.town);
            if (file == null) return null;
            var cutter = new DrivewayCutter(new KerbFrontageSource(k.kerbs));
            k.cuts = cutter.Resolve(file.requests);
            k.cutter = cutter;
            k.drivewaysSha256 = file.sha256;
            return k.cuts;
        }
    }
}

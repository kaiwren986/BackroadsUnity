using System;
using System.Collections.Generic;
using System.IO;
using Backroads.Build.Editor;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// The fictional street names (roads-R3 step 3): Assets/Backroads/Roads/Data/street-names.json. Until the user approves
    /// the list ("approved": true, with the decision id), streets get "Street NN" placeholders.
    /// </summary>
    public sealed class StreetNameList
    {
        public const string Path = "Game/Assets/Backroads/Roads/Data/street-names.json";
        public bool approved;
        public string decision = "";
        public readonly List<string> names = new List<string>();

        public static StreetNameList Load()
        {
            var l = new StreetNameList();
            var file = RepoPaths.InRepo(Path);
            if (!File.Exists(file)) return l;
            var o = BrJson.Obj(BrJson.Parse(File.ReadAllText(file)), "street-names.json");
            l.approved = o.TryGetValue("approved", out var a) && a is bool b && b;
            l.decision = o.TryGetValue("decision", out var d) ? d as string ?? "" : "";
            if (o.TryGetValue("names", out var n) && n != null)
                foreach (var x in BrJson.Arr(n)) if (x is string s && s.Trim().Length > 0) l.names.Add(s.Trim());
            return l;
        }
    }
}

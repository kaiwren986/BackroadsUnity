using Backroads.Sites.Editor;
using UnityEditor;

namespace Backroads.World.Bake
{
    /// <summary>
    /// T3.3 follow-ups (lane S's S4 request 1): lane S's site plates go into lane W's bake. Lane S registers its source
    /// ("sites", SitePlateSource) only when its Enabled flag is on, so that WorldData changes in the same merge as lane W's
    /// re-export; lane W turns it on here, once per domain load, in its own assembly (Backroads.World.Editor cannot
    /// reference lane S's editor assembly, which references it). Registering twice is harmless (the registry is by name).
    /// </summary>
    static class SitePlatesOn
    {
        [InitializeOnLoadMethod]
        static void Register() => SitePlateSource.Register();
    }
}

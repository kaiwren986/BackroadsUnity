using Backroads.Roads.Client;
using UnityEngine;

namespace Backroads.Sites.Client
{
    /// <summary>
    /// card sites-S4: a town's site meshes in a scene, drawn with lane R's road materials (BrRoadAsphalt, BrRoadConcrete,
    /// BrRoadPaint; lot asphalt is lighter through its vertex colour). Lane W's Millbrook scene calls SpawnTown (or adds
    /// this component) next to RoadChunkView; a server calls SiteChunks.Spawn without materials (colliders only).
    /// </summary>
    public sealed class SiteChunkView : MonoBehaviour
    {
        [Tooltip("TownData town id (the folder name under TownData).")]
        public string town = "";
        public bool colliders = true;

        GameObject m_Root;

        void Start()
        {
            if (!string.IsNullOrEmpty(town)) m_Root = SpawnTown(town, transform, colliders);
        }

        void OnDestroy()
        {
            if (m_Root != null) Destroy(m_Root);
        }

        public static GameObject SpawnTown(string townId, Transform parent, bool colliders = true)
        {
            var mats = RoadChunkView.LoadMaterials();
            foreach (var m in mats) if (m == null) { Debug.LogWarning("[BR] road materials missing in Roads/Client/Resources"); mats = null; break; }
            return SiteChunks.Spawn(townId, parent, mats, colliders);
        }
    }
}

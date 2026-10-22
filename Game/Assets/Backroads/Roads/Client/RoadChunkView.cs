using UnityEngine;

namespace Backroads.Roads.Client
{
    /// <summary>
    /// roads-R3: a town's road meshes in a scene. Lane W's Millbrook scene calls SpawnTown (or adds this component): the
    /// chunks of `BrRoadChunks_(town)` with LODs and colliders, drawn with the three road materials in
    /// Roads/Client/Resources (BrRoadAsphalt, BrRoadConcrete, BrRoadPaint; shader Backroads/Roads/Surface).
    /// </summary>
    public sealed class RoadChunkView : MonoBehaviour
    {
        public const string AsphaltMaterial = "BrRoadAsphalt", ConcreteMaterial = "BrRoadConcrete", PaintMaterial = "BrRoadPaint";

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

        public static Material[] LoadMaterials() => new[]
        {
            Resources.Load<Material>(AsphaltMaterial), Resources.Load<Material>(ConcreteMaterial), Resources.Load<Material>(PaintMaterial),
        };

        public static GameObject SpawnTown(string townId, Transform parent, bool colliders = true)
        {
            var set = RoadChunkSet.LoadTown(townId);
            if (set == null) { Debug.LogWarning("[BR] no road chunks baked for " + townId + " (RoadBakeCli.BakeTowns)"); return null; }
            var mats = LoadMaterials();
            foreach (var m in mats) if (m == null) { Debug.LogWarning("[BR] road materials missing in Roads/Client/Resources"); mats = null; break; }
            return RoadChunkLoader.Spawn(set, parent, mats, colliders);
        }
    }
}

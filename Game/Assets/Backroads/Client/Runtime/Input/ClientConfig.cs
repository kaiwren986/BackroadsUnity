using UnityEngine;

namespace Backroads.Client
{
    /// <summary>
    /// Resources/BrClientConfig.asset: carries Content/core/bindings.json into every client build (the table stays at its
    /// PLAN 1.2 path; this asset only references it). Written by MillbrookSceneBuilder.
    /// </summary>
    public sealed class ClientConfig : ScriptableObject
    {
        public TextAsset bindings;
    }
}

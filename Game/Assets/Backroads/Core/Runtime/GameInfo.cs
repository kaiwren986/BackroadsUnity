using UnityEngine;

namespace Backroads.Core
{
    /// <summary>
    /// Display title, company and product names and the save-folder id (PLAN 1.3, U18). ProjectSetup.Apply copies
    /// companyName and productName into PlayerSettings. Unity derives the save folder (persistentDataPath) from them,
    /// so they stay placeholders until U18 and are fixed before the first saves in M8.
    /// </summary>
    [CreateAssetMenu(menuName = "Backroads/Game Info", fileName = "GameInfo")]
    public sealed class GameInfo : ScriptableObject
    {
        public const string PlaceholderCompany = "BRDev";
        public const string PlaceholderProduct = "Backroads";

        public string displayTitle = "Backroads";
        public string companyName = PlaceholderCompany;
        public string productName = PlaceholderProduct;
        public string saveFolderId = "Backroads";
    }
}

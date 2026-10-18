using UnityEngine;

namespace Backroads.Core
{
    /// <summary>One prefix for every Backroads log line, so CI scripts can grep the player log.</summary>
    public static class Log
    {
        public const string Prefix = "[BR] ";
        public static void Info(string message) => Debug.Log(Prefix + message);
        public static void Warn(string message) => Debug.LogWarning(Prefix + message);
        public static void Error(string message) => Debug.LogError(Prefix + message);
    }
}

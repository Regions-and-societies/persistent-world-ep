using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RegionsAndSocieties.PersistentWorld.Integration
{
    /// <summary>
    /// Where the game's save flow is observed (#18, #19). Three facts the lineage needs that the world
    /// component cannot see from inside <c>ExposeData</c>: the file name a save is being written under, the
    /// file name a save was loaded from, and which files the player deleted in the save dialog.
    /// </summary>
    public static class SaveLineageHooks
    {
        /// <summary>The bare file name of the save currently being written, set just before scribing.</summary>
        public static string SavingFileName { get; private set; }

        /// <summary>The bare file name of the save most recently loaded.</summary>
        public static string LoadedFileName { get; private set; }

        private static HashSet<string> lastSeen;

        /// <summary>The save file names present at the last dialog refresh.</summary>
        private static HashSet<string> Seen() => new HashSet<string>(CensusDatabase.SavedGameNames(), System.StringComparer.OrdinalIgnoreCase);

        [HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame))]
        private static class Patch_SaveGame
        {
            [HarmonyPrefix] private static void Prefix(string fileName) { SavingFileName = fileName; }
            [HarmonyPostfix] private static void Postfix() { SavingFileName = null; }
        }

        [HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.LoadGame), typeof(string))]
        private static class Patch_LoadGame
        {
            [HarmonyPrefix] private static void Prefix(string saveFileName) { LoadedFileName = saveFileName; }
        }

        [HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.LoadGame), typeof(FileInfo))]
        private static class Patch_LoadGameFile
        {
            [HarmonyPrefix] private static void Prefix(FileInfo saveFile) { LoadedFileName = saveFile == null ? null : Path.GetFileNameWithoutExtension(saveFile.Name); }
        }

        // The save dialog deletes a file inline and then reloads its list; comparing the list before and
        // after a reload within one session tells us exactly which files the player removed.
        [HarmonyPatch(typeof(Dialog_SaveFileList), "ReloadFiles")]
        private static class Patch_ReloadFiles
        {
            [HarmonyPrefix]
            private static void Prefix()
            {
                HashSet<string> now = Seen();
                if (lastSeen != null)
                {
                    var gone = new List<string>();
                    foreach (string f in lastSeen) if (!now.Contains(f)) gone.Add(f);
                    if (gone.Count > 0) PersistentWorldComponent.Instance?.OnSavesDeleted(gone);
                }
                lastSeen = now;
            }
        }
    }
}

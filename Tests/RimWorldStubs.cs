// Runnable stubs of the RimWorld / Verse surface the pure rules layers touch. NOT shipped. Used solely
// to type-check and behaviour-test Source/ files that contain no Find, no Harmony, no Unity, in an
// environment without the game's Managed assemblies. Written from the real signatures the code meets;
// grow it only when a suite needs a new type.
using System.Collections.Generic;

namespace Verse
{
    public static class Log
    {
        public static readonly List<string> Captured = new List<string>();
        public static void Message(string s) { Captured.Add("MSG  " + s); }
        public static void Warning(string s) { Captured.Add("WARN " + s); }
        public static void Error(string s) { Captured.Add("ERR  " + s); }
    }
}

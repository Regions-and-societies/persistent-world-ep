using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RegionsAndSocieties.Demographics;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>
    /// The per-save sidecar (#6): the full materialized view — derived majority plus the linked overlay —
    /// serialized to disk for external tools and reload. A <b>rebuildable cache, never the source of
    /// truth</b>: the .rws carries the only authoritative state (the link table), and a missing or stale
    /// file is a non-event because the worker regenerates it on the cadence.
    ///
    /// <para>Pure: writes to a <see cref="TextWriter"/>, reads from a <see cref="TextReader"/>, no file
    /// paths, no game types — so the round trip is tested without a game and the writer can run on the
    /// background thread over the immutable dataset. The JSON is one self-describing document; each person
    /// is a compact numeric row in a fixed column order (see <see cref="Columns"/>). The CSV is the same
    /// rows with a header, for spreadsheets.</para>
    /// </summary>
    public static class PopulationExport
    {
        /// <summary>Bumped when the row layout changes; a reader refuses a file it does not understand.</summary>
        public const int SchemaVersion = 1;

        /// <summary>Row column order, for both formats.</summary>
        public static readonly string[] Columns =
        {
            "tile", "index", "female", "age", "ageBucket", "education", "class", "wealth", "work", "sector", "race", "faction", "ideo", "pawn",
        };

        // ---------------------------------------------------------------- JSON out

        public static void WriteJson(PopulationDataset ds, TextWriter w)
        {
            ds = ds ?? PopulationDataset.Empty;
            PopulationSnapshot s = ds.snapshot;
            w.Write("{\"schema\":"); w.Write(SchemaVersion);
            w.Write(",\"worldSeed\":"); w.Write(s.worldSeed);
            w.Write(",\"buildSerial\":"); w.Write(ds.buildSerial);
            w.Write(",\"densityVersion\":"); w.Write(s.densityVersion);
            w.Write(",\"buildMillis\":"); w.Write(ds.buildMillis);
            w.Write(",\"people\":"); w.Write(ds.Count);
            w.Write(",\"linked\":"); w.Write(ds.LinkedCount);
            w.Write(",\"regionIds\":"); WriteInts(w, s.regionIds);
            w.Write(",\"raceLabels\":"); WriteStrings(w, s.raceLabels);
            w.Write(",\"factionLabels\":"); WriteStrings(w, s.factionLabels);
            w.Write(",\"ideoLabels\":"); WriteStrings(w, s.ideoLabels);
            w.Write(",\"tiles\":[");
            for (int i = 0; i < s.tiles.Length; i++)
            {
                if (i > 0) w.Write(',');
                w.Write('['); w.Write(s.tiles[i].tile); w.Write(','); w.Write(s.tiles[i].region); w.Write(','); w.Write(s.tiles[i].population); w.Write(']');
            }
            w.Write("],\"columns\":"); WriteStrings(w, Columns);
            w.Write(",\"rows\":[");
            for (int i = 0; i < ds.people.Length; i++)
            {
                if (i > 0) w.Write(',');
                if ((i & 63) == 0) w.Write('\n');
                WriteRow(w, in ds.people[i], '[', ']', ',');
            }
            w.Write("]}\n");
        }

        // ---------------------------------------------------------------- CSV out

        public static void WriteCsv(PopulationDataset ds, TextWriter w)
        {
            ds = ds ?? PopulationDataset.Empty;
            w.Write(string.Join(",", Columns)); w.Write(",region\n");
            for (int i = 0; i < ds.people.Length; i++)
            {
                WriteRow(w, in ds.people[i], '\0', '\0', ',');
                w.Write(','); w.Write(ds.RegionOf(in ds.people[i])); w.Write('\n');
            }
        }

        private static void WriteRow(TextWriter w, in Individual p, char open, char close, char sep)
        {
            if (open != '\0') w.Write(open);
            w.Write(p.tile); w.Write(sep); w.Write(p.index); w.Write(sep); w.Write(p.female ? 1 : 0); w.Write(sep);
            w.Write(p.age); w.Write(sep); w.Write((int)p.ageBucket); w.Write(sep); w.Write((int)p.education); w.Write(sep);
            w.Write((int)p.ses); w.Write(sep); w.Write(p.wealth); w.Write(sep); w.Write((int)p.work); w.Write(sep);
            w.Write((int)p.sector); w.Write(sep); w.Write(p.raceKey); w.Write(sep); w.Write(p.factionKey); w.Write(sep);
            w.Write(p.ideoKey); w.Write(sep); w.Write(p.pawnId);
            if (close != '\0') w.Write(close);
        }

        // ---------------------------------------------------------------- JSON in

        /// <summary>Parse a sidecar written by <see cref="WriteJson"/> back into a dataset. Returns null for
        /// anything it cannot trust: unreadable text, a different schema, rows that do not match the tile
        /// table. Never throws. The rebuilt snapshot carries the tiles, regions and labels but empty
        /// profiles — it is a restored <i>result</i>, not something to rebuild from.</summary>
        public static PopulationDataset ReadJson(TextReader r)
        {
            try
            {
                object root = MiniJson.Parse(r.ReadToEnd());
                if (!(root is Dictionary<string, object> o)) return null;
                if (Int(o, "schema") != SchemaVersion) return null;

                int[] regionIds = Ints(o, "regionIds");
                string[] races = Strings(o, "raceLabels"), factions = Strings(o, "factionLabels"), ideos = Strings(o, "ideoLabels");
                var tileList = o["tiles"] as List<object>;
                var rowList = o["rows"] as List<object>;
                if (tileList == null || rowList == null) return null;

                var tiles = new TileSlot[tileList.Count];
                long total = 0;
                for (int i = 0; i < tiles.Length; i++)
                {
                    var t = tileList[i] as List<object>;
                    if (t == null || t.Count < 3) return null;
                    tiles[i] = new TileSlot { tile = ToInt(t[0]), region = ToInt(t[1]), population = ToInt(t[2]) };
                    if (i > 0 && tiles[i].tile <= tiles[i - 1].tile) return null;   // must be ascending
                    if (tiles[i].population > 0) total += tiles[i].population;
                }
                if (total != rowList.Count) return null;

                var snapshot = new PopulationSnapshot(Int(o, "worldSeed"), tiles, regionIds, new RegionProfile[regionIds.Length],
                    races, factions, ideos, Int(o, "densityVersion"));

                var tileStart = new int[tiles.Length + 1];
                for (int i = 0; i < tiles.Length; i++) tileStart[i + 1] = tileStart[i] + Math.Max(0, tiles[i].population);

                var people = new Individual[rowList.Count];
                int linked = 0, ti = 0;
                for (int i = 0; i < people.Length; i++)
                {
                    var c = rowList[i] as List<object>;
                    if (c == null || c.Count < Columns.Length) return null;
                    var p = new Individual
                    {
                        tile = ToInt(c[0]), index = ToInt(c[1]), female = ToInt(c[2]) != 0, age = ToInt(c[3]),
                        ageBucket = (AgeBucket)ToInt(c[4]), education = (EducationTier)ToInt(c[5]), ses = (SesTier)ToInt(c[6]),
                        wealth = ToInt(c[7]), work = (WorkStatus)ToInt(c[8]), sector = (OccupationSector)ToInt(c[9]),
                        raceKey = ToInt(c[10]), factionKey = ToInt(c[11]), ideoKey = ToInt(c[12]), pawnId = ToInt(c[13]),
                    };
                    // Rows must sit exactly where the tile table says they do.
                    while (ti < tiles.Length && i >= tileStart[ti + 1]) ti++;
                    if (ti >= tiles.Length || p.tile != tiles[ti].tile || p.index != i - tileStart[ti]) return null;
                    if (p.pawnId != 0) linked++;
                    people[i] = p;
                }

                return new PopulationDataset(snapshot, people, tileStart, Long(o, "buildMillis"), linked);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A filesystem-safe sidecar name for a world id, e.g. <c>population_1a2b3c4d.json</c>.</summary>
        public static string FileName(string worldId, string extension)
        {
            var sb = new StringBuilder("population_");
            foreach (char ch in worldId ?? "world")
                sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
            if (sb.Length == "population_".Length) sb.Append("world");
            sb.Append('.').Append(extension.TrimStart('.'));
            return sb.ToString();
        }

        // ---------------------------------------------------------------- helpers

        private static void WriteInts(TextWriter w, int[] a)
        {
            w.Write('[');
            for (int i = 0; i < a.Length; i++) { if (i > 0) w.Write(','); w.Write(a[i]); }
            w.Write(']');
        }

        private static void WriteStrings(TextWriter w, string[] a)
        {
            w.Write('[');
            for (int i = 0; i < a.Length; i++) { if (i > 0) w.Write(','); WriteString(w, a[i]); }
            w.Write(']');
        }

        private static void WriteString(TextWriter w, string s)
        {
            if (s == null) { w.Write("null"); return; }
            w.Write('"');
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': w.Write("\\\""); break;
                    case '\\': w.Write("\\\\"); break;
                    case '\n': w.Write("\\n"); break;
                    case '\r': w.Write("\\r"); break;
                    case '\t': w.Write("\\t"); break;
                    default: if (ch < ' ') { w.Write("\\u"); w.Write(((int)ch).ToString("x4")); } else w.Write(ch); break;
                }
            }
            w.Write('"');
        }

        private static int Int(Dictionary<string, object> o, string key) => o.TryGetValue(key, out object v) ? ToInt(v) : 0;
        private static long Long(Dictionary<string, object> o, string key) => o.TryGetValue(key, out object v) && v is double d ? (long)d : 0;
        private static int ToInt(object v) => v is double d ? checked((int)d) : throw new FormatException("number expected");

        private static int[] Ints(Dictionary<string, object> o, string key)
        {
            var list = o.TryGetValue(key, out object v) ? v as List<object> : null;
            if (list == null) return Array.Empty<int>();
            var a = new int[list.Count];
            for (int i = 0; i < a.Length; i++) a[i] = ToInt(list[i]);
            return a;
        }

        private static string[] Strings(Dictionary<string, object> o, string key)
        {
            var list = o.TryGetValue(key, out object v) ? v as List<object> : null;
            if (list == null) return Array.Empty<string>();
            var a = new string[list.Count];
            for (int i = 0; i < a.Length; i++) a[i] = list[i] as string;
            return a;
        }
    }

    /// <summary>
    /// The smallest JSON reader that covers what <see cref="PopulationExport"/> writes: objects, arrays,
    /// strings, numbers, true/false/null. Numbers come back as <see cref="double"/>, objects as
    /// <c>Dictionary&lt;string, object&gt;</c>, arrays as <c>List&lt;object&gt;</c>. No dependency, so the
    /// reload path needs neither Newtonsoft nor Unity's JsonUtility and runs in the test harness.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string text)
        {
            int i = 0;
            object v = Value(text, ref i);
            Space(text, ref i);
            if (i != text.Length) throw new FormatException("trailing content");
            return v;
        }

        private static object Value(string s, ref int i)
        {
            Space(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return Object(s, ref i);
            if (c == '[') return Array(s, ref i);
            if (c == '"') return String(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return Number(s, ref i);
        }

        private static Dictionary<string, object> Object(string s, ref int i)
        {
            var o = new Dictionary<string, object>();
            i++;   // {
            Space(s, ref i);
            if (s[i] == '}') { i++; return o; }
            while (true)
            {
                Space(s, ref i);
                string key = String(s, ref i);
                Space(s, ref i);
                if (s[i] != ':') throw new FormatException("':' expected");
                i++;
                o[key] = Value(s, ref i);
                Space(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return o; }
                throw new FormatException("',' or '}' expected");
            }
        }

        private static List<object> Array(string s, ref int i)
        {
            var a = new List<object>();
            i++;   // [
            Space(s, ref i);
            if (s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(Value(s, ref i));
                Space(s, ref i);
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new FormatException("',' or ']' expected");
            }
        }

        private static string String(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("string expected");
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("bad escape");
                }
            }
        }

        private static double Number(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
            if (i == start) throw new FormatException("value expected at " + i);
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static void Expect(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException(word + " expected");
            i += word.Length;
        }

        private static void Space(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\n' || s[i] == '\r' || s[i] == '\t')) i++;
        }
    }
}

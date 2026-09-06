namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>
    /// A person's identity (#9): a 64-bit mix of the world seed, the tile they were born on, and their
    /// birth index there. Opaque, unique per world for all practical purposes, and never changes — a person
    /// can move anywhere and keep their id, their relationships, and their history. Zero is reserved for
    /// "no person". Identity says where you were <i>born</i>; where you live is a mutable attribute.
    /// </summary>
    public static class PersonId
    {
        /// <summary>The id of the person born at index <paramref name="birthIndex"/> on <paramref name="birthTile"/>.</summary>
        public static long Make(int worldSeed, int birthTile, int birthIndex)
        {
            unchecked
            {
                ulong x = ((ulong)(uint)worldSeed << 32) | (uint)birthTile;
                x ^= (ulong)(uint)birthIndex * 0x9E3779B97F4A7C15ul;
                x = Mix(x + 0x632BE59BD9B4E019ul);
                x ^= Mix(x ^ ((ulong)(uint)birthIndex << 20) ^ (uint)birthTile);
                long id = (long)x;
                return id == 0 ? 1 : id;
            }
        }

        // splitmix64 finalizer — strong avalanche, cheap, deterministic.
        private static ulong Mix(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ul;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBul;
                return z ^ (z >> 31);
            }
        }

        /// <summary>The id as it appears in exports: sixteen hex digits, unsigned.</summary>
        public static string ToHex(long id) => ((ulong)id).ToString("x16");

        /// <summary>Parse <see cref="ToHex"/> output. False for anything else.</summary>
        public static bool TryParseHex(string hex, out long id)
        {
            id = 0;
            if (hex == null || hex.Length == 0 || hex.Length > 16) return false;
            if (!ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong u)) return false;
            id = (long)u;
            return true;
        }
    }
}

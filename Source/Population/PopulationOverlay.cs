using System;
using System.Collections.Generic;

namespace RegionsAndSocieties.PersistentWorld.Population
{
    /// <summary>One person's difference from birth: where they live now, and whether they are still alive.</summary>
    public struct PersonDelta
    {
        public const byte FlagDead = 1;

        public long id;
        public int birthTile;    // so the build can find the baseline row without an id lookup
        public int birthIndex;
        public int homeTile;     // current home; equals birthTile when only the flags differ
        public byte flags;

        public bool Dead => (flags & FlagDead) != 0;
        public bool Moved => homeTile != birthTile;
        /// <summary>True when nothing differs from birth — the record can be dropped.</summary>
        public bool IsIdentity => !Moved && flags == 0;

        /// <summary>Bytes per packed record: id, birthTile, birthIndex, homeTile, flags.</summary>
        public const int PackedSize = 8 + 4 + 4 + 4 + 1;
    }

    /// <summary>
    /// The sparse mutable overlay (#9): state, not history. The baseline is the deterministic birth list;
    /// this holds one record per person whose state differs from birth. A person who never changed costs
    /// nothing, a person who moved five times costs one record, so storage is bounded by how many people
    /// ever changed, never by elapsed time. The only authoritative per-person state, and it rides inside
    /// the .rws packed (<see cref="ToBytes"/>). Pure: no game types, so it is tested without one.
    /// </summary>
    public sealed class PopulationOverlay
    {
        private readonly Dictionary<long, PersonDelta> byId = new Dictionary<long, PersonDelta>();

        public int Count => byId.Count;

        public bool TryGet(long id, out PersonDelta delta) => byId.TryGetValue(id, out delta);

        /// <summary>Where a person lives now, or their birth tile when they never moved.</summary>
        public int HomeOf(long id, int birthTile) => byId.TryGetValue(id, out PersonDelta d) ? d.homeTile : birthTile;

        /// <summary>Write a record; an identity record (nothing differs) is removed instead of stored.</summary>
        public void Set(PersonDelta delta)
        {
            if (delta.id == 0) return;
            if (delta.IsIdentity) byId.Remove(delta.id);
            else byId[delta.id] = delta;
        }

        /// <summary>Move a person. Returns true when their home actually changed.</summary>
        public bool Move(long id, int birthTile, int birthIndex, int homeTile)
        {
            if (id == 0) return false;
            if (!byId.TryGetValue(id, out PersonDelta d))
                d = new PersonDelta { id = id, birthTile = birthTile, birthIndex = birthIndex, homeTile = birthTile };
            if (d.homeTile == homeTile) return false;
            d.homeTile = homeTile;
            Set(d);
            return true;
        }

        /// <summary>Mark a person dead; they leave the materialized list and never come back.</summary>
        public void MarkDead(long id, int birthTile, int birthIndex)
        {
            if (id == 0) return;
            if (!byId.TryGetValue(id, out PersonDelta d))
                d = new PersonDelta { id = id, birthTile = birthTile, birthIndex = birthIndex, homeTile = birthTile };
            d.flags |= PersonDelta.FlagDead;
            byId[id] = d;
        }

        public bool Remove(long id) => byId.Remove(id);

        public void Clear() => byId.Clear();

        /// <summary>Every record, sorted by id — the immutable copy a snapshot carries.</summary>
        public PersonDelta[] Records()
        {
            var arr = new PersonDelta[byId.Count];
            byId.Values.CopyTo(arr, 0);
            Array.Sort(arr, (a, b) => a.id.CompareTo(b.id));
            return arr;
        }

        /// <summary>Pack every record for the save. <see cref="PersonDelta.PackedSize"/> bytes each, sorted by id.</summary>
        public byte[] ToBytes()
        {
            PersonDelta[] recs = Records();
            var bytes = new byte[recs.Length * PersonDelta.PackedSize];
            int o = 0;
            foreach (PersonDelta d in recs)
            {
                WriteLong(bytes, ref o, d.id);
                WriteInt(bytes, ref o, d.birthTile);
                WriteInt(bytes, ref o, d.birthIndex);
                WriteInt(bytes, ref o, d.homeTile);
                bytes[o++] = d.flags;
            }
            return bytes;
        }

        /// <summary>Replace the table from <see cref="ToBytes"/> output. A null, empty, or malformed
        /// (wrong length) buffer loads nothing. Returns the number of records loaded.</summary>
        public int Load(byte[] bytes)
        {
            byId.Clear();
            if (bytes == null || bytes.Length == 0 || bytes.Length % PersonDelta.PackedSize != 0) return 0;
            int o = 0;
            while (o < bytes.Length)
            {
                var d = new PersonDelta();
                d.id = ReadLong(bytes, ref o);
                d.birthTile = ReadInt(bytes, ref o);
                d.birthIndex = ReadInt(bytes, ref o);
                d.homeTile = ReadInt(bytes, ref o);
                d.flags = bytes[o++];
                if (d.id != 0 && !d.IsIdentity) byId[d.id] = d;
            }
            return byId.Count;
        }

        private static void WriteInt(byte[] b, ref int o, int v) { for (int i = 0; i < 4; i++) b[o++] = (byte)(v >> (8 * i)); }
        private static void WriteLong(byte[] b, ref int o, long v) { for (int i = 0; i < 8; i++) b[o++] = (byte)(v >> (8 * i)); }
        private static int ReadInt(byte[] b, ref int o) { int v = 0; for (int i = 0; i < 4; i++) v |= b[o++] << (8 * i); return v; }
        private static long ReadLong(byte[] b, ref int o) { long v = 0; for (int i = 0; i < 8; i++) v |= (long)b[o++] << (8 * i); return v; }
    }
}

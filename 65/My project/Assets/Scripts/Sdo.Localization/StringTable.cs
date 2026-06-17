using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sdo.Localization
{
    /// <summary>
    /// One language's key→string table. Pure logic (no file IO), so it is unit-testable.
    /// Wire format is a JsonUtility-friendly array schema (same trick as SongCatalog):
    /// <code>{ "language":"en", "name":"English", "culture":"en-US",
    ///         "entries":[ {"k":"lobby.create_room","v":"Create Room"}, ... ] }</code>
    /// (JsonUtility can't deserialize a Dictionary, hence the entries[] array.)
    /// </summary>
    public sealed class StringTable
    {
        [Serializable] private class Row { public string k; public string v; }
        [Serializable] private class Doc { public string language; public string name; public string culture; public Row[] entries; }

        private readonly Dictionary<string, string> _map;

        public string LanguageCode { get; }
        public string DisplayName { get; }
        public string Culture { get; }
        public int Count => _map.Count;
        public IEnumerable<string> Keys => _map.Keys;

        private StringTable(Doc d)
        {
            LanguageCode = d.language ?? "";
            DisplayName = string.IsNullOrEmpty(d.name) ? LanguageCode : d.name;
            Culture = string.IsNullOrEmpty(d.culture) ? "en-US" : d.culture;
            _map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (d.entries != null)
                foreach (var r in d.entries)
                    if (r != null && !string.IsNullOrEmpty(r.k))
                        _map[r.k] = r.v ?? "";
        }

        /// <summary>Parse a localization json document. Throws on malformed input.</summary>
        public static StringTable Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("empty localization json");
            var d = JsonUtility.FromJson<Doc>(json);
            if (d == null) throw new ArgumentException("invalid localization json");
            return new StringTable(d);
        }

        public bool TryGet(string key, out string value) => _map.TryGetValue(key, out value);
    }
}

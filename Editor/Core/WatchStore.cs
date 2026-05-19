using System;
using System.Collections.Generic;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Project-local file-backed list of watch expressions. Payload
    /// lives in <c>&lt;project&gt;/UserSettings/RoslynRepl/watches.json</c>,
    /// scoped to the project folder so deleting the project reclaims
    /// the data in one go.
    ///
    /// Issue #62 promoted the on-disk shape from a plain
    /// <c>List&lt;string&gt;</c> (envelope v1) to a list of
    /// <see cref="WatchEntry"/> (envelope v2). v1 files load
    /// automatically — each string becomes an enabled <c>WatchEntry</c>
    /// — and the next <see cref="Persist"/> rewrites the file in v2
    /// shape, so the migration is transparent and one-way.
    /// </summary>
    public static class WatchStore
    {
        public static event Action Changed;

        private const string FileName = "watches.json";
        private const int CurrentVersion = 2;

        // v2: each row carries its enabled flag.
        [Serializable]
        private sealed class EnvelopeV2
        {
            public int version = CurrentVersion;
            public List<WatchEntry> items = new List<WatchEntry>();
        }

        // v1: plain string list. Kept around for migration only —
        // never written back; the next Persist after a Load drops
        // the file in v2 shape.
        [Serializable]
        private sealed class EnvelopeV1
        {
            public int version;
            public List<string> items = new List<string>();
        }

        // Used to peek at the version field without committing to a
        // specific envelope shape. JsonUtility tolerates extra fields
        // on either side, so reading this against a v2 payload
        // surfaces version=2 even though the items field type
        // doesn't match.
        [Serializable]
        private sealed class VersionProbe
        {
            public int version;
        }

        /// <summary>
        /// Return the list of watches the evaluator should actually
        /// execute — i.e. only the enabled entries. Same shape callers
        /// have used since the package shipped, so external consumers
        /// don't see the entry model migration as a breaking change.
        /// </summary>
        public static List<string> Load()
        {
            var entries = LoadEntries();
            var result = new List<string>(entries.Count);
            foreach (var e in entries)
            {
                if (e == null) continue;
                if (!e.enabled) continue;
                if (string.IsNullOrWhiteSpace(e.expression)) continue;
                result.Add(e.expression);
            }
            return result;
        }

        /// <summary>
        /// Full entry list — enabled and disabled rows, in persisted
        /// order. UI and the evaluator both use this so a row's
        /// toggle state is visible without re-reading the file.
        /// </summary>
        public static List<WatchEntry> LoadEntries()
        {
            if (!UserSettingsStorage.TryReadAllText(FileName, out var json))
                return new List<WatchEntry>();
            return DecodeJson(json);
        }

        /// <summary>True iff the on-disk file currently exists. Used
        /// by Reset Project Data to detect "file is there but Load
        /// returned an empty list" — a corrupt JSON, a locked file,
        /// or any other read failure all collapse to an empty list,
        /// and counting only <c>Load().Count</c> would skip the wipe
        /// in exactly the cases the user most needs Reset to handle.
        /// PR-review followup on #27.</summary>
        public static bool HasAny() => UserSettingsStorage.Exists(FileName);

        public static void Add(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression)) return;
            expression = expression.Trim();
            var list = LoadEntries();
            // Skip exact duplicates so quick double-Enter doesn't add the
            // same row twice. Different whitespace is preserved as
            // intentional (different expression in the user's mind).
            for (int i = 0; i < list.Count; i++)
                if (list[i]?.expression == expression) return;
            list.Add(new WatchEntry { expression = expression, enabled = true });
            Persist(list);
        }

        public static void Remove(string expression)
        {
            var list = LoadEntries();
            int removed = list.RemoveAll(e => e?.expression == expression);
            if (removed > 0) Persist(list);
        }

        /// <summary>
        /// Flip a single row's enabled flag. No-op when the
        /// expression isn't in the list or the flag is already at
        /// the target value, so the Watch panel can wire this
        /// directly to a toggle's ValueChangedCallback without
        /// guarding against the redundant fire UI Toolkit emits
        /// when the user clicks the toggle to its current state.
        /// </summary>
        public static void SetEnabled(string expression, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(expression)) return;
            var list = LoadEntries();
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null || e.expression != expression) continue;
                if (e.enabled == enabled) return;
                e.enabled = enabled;
                Persist(list);
                return;
            }
        }

        /// <summary>
        /// Bulk toggle for the header's "Enable all" / "Disable all"
        /// affordances. Only persists when at least one row actually
        /// changed so a repeated click against an already-uniform
        /// state doesn't write the file and re-fire Changed.
        /// </summary>
        public static void SetAllEnabled(bool enabled)
        {
            var list = LoadEntries();
            bool changed = false;
            foreach (var e in list)
            {
                if (e == null) continue;
                if (e.enabled == enabled) continue;
                e.enabled = enabled;
                changed = true;
            }
            if (changed) Persist(list);
        }

        /// <summary>Wipe persisted watches. Returns the success flag
        /// from <see cref="UserSettingsStorage.Delete"/> (true =
        /// post-call file does not exist) so callers like Reset
        /// Project Data can aggregate "did everything actually go
        /// away?" — a stuck file would otherwise be invisible behind
        /// the Changed event we always fire. PR-review followup on
        /// #27.</summary>
        public static bool Clear()
        {
            bool ok = UserSettingsStorage.Delete(FileName);
            Changed?.Invoke();
            return ok;
        }

        private static void Persist(List<WatchEntry> list)
        {
            var env = new EnvelopeV2 { items = new List<WatchEntry>() };
            foreach (var e in list)
            {
                if (e == null) continue;
                if (string.IsNullOrWhiteSpace(e.expression)) continue;
                env.items.Add(new WatchEntry
                {
                    expression = e.expression.Trim(),
                    enabled = e.enabled,
                });
            }
            UserSettingsStorage.WriteAllText(FileName, JsonUtility.ToJson(env, prettyPrint: true));
            Changed?.Invoke();
        }

        private static List<WatchEntry> DecodeJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return new List<WatchEntry>();

            // Peek at the version field first; that lets us pick the
            // right envelope class instead of relying on JsonUtility's
            // tolerant-deserialisation behaviour (which would
            // silently produce empty entry lists for v1 files).
            int version;
            try
            {
                var probe = JsonUtility.FromJson<VersionProbe>(json);
                version = probe?.version ?? 1;
            }
            catch
            {
                return new List<WatchEntry>();
            }

            if (version >= 2)
            {
                try
                {
                    var env = JsonUtility.FromJson<EnvelopeV2>(json);
                    return env?.items ?? new List<WatchEntry>();
                }
                catch
                {
                    return new List<WatchEntry>();
                }
            }

            // v1 (or unversioned) — string list. Wrap each non-empty
            // string in an enabled entry so the existing watch carries
            // forward intact. The next Persist call rewrites the file
            // in v2 shape, so the migration is one-time per project.
            try
            {
                var env = JsonUtility.FromJson<EnvelopeV1>(json);
                if (env?.items == null) return new List<WatchEntry>();
                var result = new List<WatchEntry>(env.items.Count);
                foreach (var s in env.items)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    result.Add(new WatchEntry { expression = s.Trim(), enabled = true });
                }
                return result;
            }
            catch
            {
                return new List<WatchEntry>();
            }
        }
    }
}

using System;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// One row in the persisted watch list. Issue #62 promoted the
    /// shape from a plain <c>List&lt;string&gt;</c> to an entry model
    /// so each watch can carry its enabled / disabled flag alongside
    /// the expression. Disabled rows stay in the file (so the user
    /// doesn't lose them) but the evaluator skips them on every
    /// Refresh, sparing the side effects of a heavy expression while
    /// keeping it one toggle away from re-activation.
    ///
    /// Marked <see cref="SerializableAttribute"/> so Unity's
    /// <c>JsonUtility</c> round-trips it inside the file envelope.
    /// Field names match the JSON key casing the envelope writes
    /// (lower-cased, matches the previous v1 shape's <c>items</c>
    /// list of strings).
    /// </summary>
    [Serializable]
    public sealed class WatchEntry
    {
        public string expression;
        public bool enabled = true;
    }
}

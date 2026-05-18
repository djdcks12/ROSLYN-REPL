using System.Collections.Generic;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// One row in the result tree shown by the REPL output panel. The tree is
    /// produced eagerly by <see cref="SimpleObjectSerializer"/> with depth and
    /// collection-head caps so even large object graphs don't explode.
    /// </summary>
    public class ReplValueNode
    {
        public string Name;          // field/property name, "[index]" for collection items, or "(result)" for the root
        public string TypeName;      // short type name (e.g. "List<int>")
        public string Preview;       // 1-line value preview
        public bool IsExpandable;    // true iff Children may be non-empty
        public List<ReplValueNode> Children = new();

        /// <summary>
        /// Issue #61: the live value behind this row. Set by
        /// <see cref="SimpleObjectSerializer.ToTree"/> for every
        /// non-placeholder node so Output / Watch tree row actions
        /// can re-inspect, set-as-`_`, or copy the value without
        /// re-evaluating the user's expression. Null when the row
        /// represents a placeholder (collection truncation marker,
        /// error stand-in, etc.) or when the value itself is null.
        /// </summary>
        public object Value;

        /// <summary>
        /// Issue #61: a C# expression that resolves back to this
        /// row's value when evaluated against the current REPL
        /// context. Built by accumulating the parent's path with the
        /// node's accessor: <c>_</c> → <c>_.field</c> → <c>_.field[3]</c>
        /// → <c>_.field[3].sub["foo"]</c>. Null when the path
        /// can't be expressed safely — e.g. a dictionary with a
        /// non-primitive key, a placeholder / error node — so the
        /// "Add Watch" action can disable itself rather than emit
        /// broken syntax.
        /// </summary>
        public string ExpressionPath;

        /// <summary>
        /// Issue #61: the value <see cref="SimpleObjectSerializer.ToTree"/>
        /// was rooted at when this node was built. Every descendant of
        /// a single tree shares the same reference, so menu handlers
        /// can ask "is `_` still the same object the visible tree was
        /// built against?" before emitting an <see cref="ExpressionPath"/>-
        /// based action. Path expressions start with the root token
        /// (<c>_</c> by default, or the Watch row's expression) and
        /// only resolve correctly while that token still points at
        /// this same instance — a later Set as <c>_</c> against
        /// a different tree would silently re-aim every old path
        /// otherwise.
        /// </summary>
        public object RootValue;
    }
}

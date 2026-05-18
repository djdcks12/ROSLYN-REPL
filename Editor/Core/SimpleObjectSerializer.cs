using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace RoslynRepl.Editor.Core
{
    /// <summary>
    /// Reflection-based object → <see cref="ReplValueNode"/> tree converter.
    /// Walks fields (incl. private + inherited) and readable instance properties
    /// of the target object, recursively, with cycle detection, depth caps, and
    /// collection-head truncation. Designed to be safe for large or self-
    /// referential graphs.
    /// </summary>
    public static class SimpleObjectSerializer
    {
        public class Options
        {
            public int MaxDepth { get; set; } = 4;
            public int CollectionHeadCount { get; set; } = 50;
            public bool IncludeNonPublic { get; set; } = true;
            public bool IncludeProperties { get; set; } = true;
            // Hard cap on the total node count produced by a single ToTree call.
            // Plain C# managers often hold Action events whose invocation lists
            // reach view-side MonoBehaviours, exploding the graph. Even with
            // depth caps, fan-out can produce tens of thousands of nodes and
            // freeze the Editor. The cap aborts cleanly with a marker leaf.
            public int MaxTotalNodes { get; set; } = 2000;
        }

        private class BuildState
        {
            public Options Options;
            public HashSet<object> Visited;
            public int NodeCount;
            // Issue #61: the root value every ExpressionPath in the
            // tree is rooted at. Stamped onto every node so context-
            // menu handlers can compare against ReplEngine.LastResult
            // and detect that the visible tree has gone stale relative
            // to `_`. A null root is legal (ToTree(null, ...) builds
            // a "null" leaf) — same null gets stamped on every node
            // and the menu can treat that as "no root to anchor on".
            public object RootValue;
        }

        public static ReplValueNode ToTree(object value, Options options = null, string rootPath = "_")
        {
            options ??= new Options();
            var state = new BuildState
            {
                Options = options,
                Visited = new HashSet<object>(ReferenceEqualityComparer.Instance),
                NodeCount = 0,
                RootValue = value,
            };
            // rootPath defaults to "_" because every UI-driven ToTree
            // call site assigns the value to ReplEngine.LastResult on
            // the same beat (Run / Browse / Reinspect), so `_` resolves
            // to the same instance. WatchEvaluator overrides with the
            // user's actual expression so its sub-tree paths grow off
            // the watch row's accessor rather than a stale `_`.
            return BuildNode("(result)", value, depth: 0, state, rootPath);
        }

        private static ReplValueNode BuildNode(
            string name, object value, int depth, BuildState state, string path)
        {
            state.NodeCount++;
            if (state.NodeCount > state.Options.MaxTotalNodes)
            {
                // Placeholder — Value/ExpressionPath stay null so
                // context menus disable Inspect / Set as `_` /
                // Add Watch for the truncation marker. RootValue is
                // still stamped so any descendants (there aren't
                // any — this is a leaf placeholder) inherit the same
                // anchor identity as the rest of the tree.
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = "",
                    Preview = $"(node cap reached at {state.Options.MaxTotalNodes}; subtree truncated)",
                    IsExpandable = false,
                    RootValue = state.RootValue,
                };
            }

            if (value == null)
            {
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = "null",
                    Preview = "null",
                    IsExpandable = false,
                    Value = null,
                    ExpressionPath = path,
                    RootValue = state.RootValue,
                };
            }

            var type = value.GetType();
            var typeName = TypeFormatter.Short(type);

            if (value is UnityEngine.Object uo && uo == null)
            {
                // Destroyed Unity object — keep the path so the user
                // can still copy a snippet that references the
                // (now-broken) accessor, but Value stays null so
                // Inspect / Set as `_` correctly grey out.
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = $"{typeName} (missing/destroyed)",
                    IsExpandable = false,
                    Value = null,
                    ExpressionPath = path,
                    RootValue = state.RootValue,
                };
            }

            if (IsLeafType(type))
            {
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = ValueFormatter.Format(value),
                    IsExpandable = false,
                    Value = value,
                    ExpressionPath = path,
                    RootValue = state.RootValue,
                };
            }

            if (!type.IsValueType && state.Visited.Contains(value))
            {
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = "[circular reference]",
                    IsExpandable = false,
                    Value = value,
                    ExpressionPath = path,
                    RootValue = state.RootValue,
                };
            }

            if (depth >= state.Options.MaxDepth)
            {
                return new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = ValueFormatter.Format(value) + "  …(depth limit)",
                    IsExpandable = false,
                    Value = value,
                    ExpressionPath = path,
                    RootValue = state.RootValue,
                };
            }

            bool added = false;
            if (!type.IsValueType)
            {
                state.Visited.Add(value);
                added = true;
            }

            try
            {
                var node = new ReplValueNode
                {
                    Name = name,
                    TypeName = typeName,
                    Preview = ValueFormatter.Format(value),
                    IsExpandable = true,
                    Value = value,
                    ExpressionPath = path,
                    RootValue = state.RootValue
                };

                if (value is IDictionary dict)
                {
                    node.Children = BuildDictChildren(dict, depth, state, path);
                }
                else if (value is IEnumerable enumerable && !(value is string))
                {
                    node.Children = BuildEnumerableChildren(enumerable, depth, state, path);
                }
                else
                {
                    node.Children = BuildMemberChildren(value, type, depth, state, path);
                }

                if (node.Children.Count == 0)
                    node.IsExpandable = false;

                return node;
            }
            finally
            {
                if (added) state.Visited.Remove(value);
            }
        }

        private static List<ReplValueNode> BuildMemberChildren(
            object obj, Type type, int depth, BuildState state, string parentPath)
        {
            var children = new List<ReplValueNode>();
            var bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            if (state.Options.IncludeNonPublic) bf |= BindingFlags.NonPublic;

            // Walk type hierarchy so base-class fields are visible too.
            var seenFieldNames = new HashSet<string>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var f in t.GetFields(bf).OrderBy(f => f.Name))
                {
                    if (f.IsStatic) continue;
                    // Skip compiler-generated backing fields visually noisy
                    if (f.Name.Contains("k__BackingField")) continue;
                    if (!seenFieldNames.Add(f.Name)) continue;

                    object v;
                    try { v = f.GetValue(obj); }
                    catch (Exception ex) { children.Add(ErrorNode(f.Name, ex, state)); continue; }

                    // Field accessors propagate the parent's path
                    // only when the field name is itself a path-safe
                    // C# identifier. Compiler-generated fields on
                    // anonymous / state-machine / display types
                    // ("<A>i__Field", "<>1__state", etc.) are
                    // reflection-visible because IncludeNonPublic
                    // defaults to true, but their names start with
                    // `<` so neither the Roslyn compile path nor
                    // WatchEvaluator.TryReadIdentifier can resolve
                    // them. Emit them as display rows (the user can
                    // still read the value) but null out the path
                    // so Add Watch / Copy Path disable themselves
                    // rather than write an unparseable expression.
                    string childPath = (parentPath == null || !IsSafeMemberIdentifier(f.Name))
                        ? null
                        : parentPath + "." + f.Name;
                    children.Add(BuildNode(f.Name, v, depth + 1, state, childPath));
                    if (state.NodeCount > state.Options.MaxTotalNodes) return children;
                }
            }

            if (state.Options.IncludeProperties)
            {
                // Only the most-derived declaration of each property to avoid
                // duplicates from `new` shadowing or virtual overrides.
                var seenPropNames = new HashSet<string>();
                var pbf = BindingFlags.Public | BindingFlags.Instance;
                if (state.Options.IncludeNonPublic) pbf |= BindingFlags.NonPublic;

                foreach (var p in type.GetProperties(pbf).OrderBy(p => p.Name))
                {
                    if (!p.CanRead) continue;
                    if (p.GetIndexParameters().Length > 0) continue;
                    var getter = p.GetMethod;
                    if (getter == null || getter.IsStatic) continue;
                    if (!seenPropNames.Add(p.Name)) continue;

                    // Skip properties whose *declaring type* lives in a
                    // Unity-shipped assembly. Unity's accessors (Image.color,
                    // Renderer.bounds, Canvas.worldCamera, Transform.position…)
                    // commonly call into native code; degenerate state fires
                    // "Assertion failed" entries that bypass managed try/catch
                    // and spam the Console. Properties declared on user types
                    // (MonoBehaviour / ScriptableObject subclasses, etc.) are
                    // walked normally — that's where user intent lives.
                    if (IsUnityFrameworkType(p.DeclaringType)) continue;

                    object v;
                    try { v = p.GetValue(obj); }
                    catch (TargetInvocationException tie)
                    { children.Add(ErrorNode(p.Name, tie.InnerException ?? tie, state)); continue; }
                    catch (Exception ex)
                    { children.Add(ErrorNode(p.Name, ex, state)); continue; }

                    // Same identifier gating as the field path —
                    // explicit-impl properties ("X.Y") and any other
                    // form Reflection can surface but the parser
                    // can't read get a null child path.
                    string childPath = (parentPath == null || !IsSafeMemberIdentifier(p.Name))
                        ? null
                        : parentPath + "." + p.Name;
                    children.Add(BuildNode(p.Name, v, depth + 1, state, childPath));
                    if (state.NodeCount > state.Options.MaxTotalNodes) return children;
                }
            }

            return children;
        }

        private static List<ReplValueNode> BuildEnumerableChildren(
            IEnumerable enumerable, int depth, BuildState state, string parentPath)
        {
            var children = new List<ReplValueNode>();
            int idx = 0;
            try
            {
                foreach (var item in enumerable)
                {
                    if (idx >= state.Options.CollectionHeadCount)
                    {
                        children.Add(new ReplValueNode
                        {
                            Name = "...",
                            TypeName = "",
                            Preview = $"(remaining items truncated at {state.Options.CollectionHeadCount})",
                            IsExpandable = false,
                            RootValue = state.RootValue,
                        });
                        break;
                    }
                    // Numeric indexer is always C#-safe: `parent[0]`,
                    // `parent[1]`, …. Inherits parent's safe/unsafe
                    // lineage like every other path-accumulating step.
                    string childPath = parentPath == null
                        ? null
                        : parentPath + "[" + idx + "]";
                    children.Add(BuildNode($"[{idx}]", item, depth + 1, state, childPath));
                    idx++;
                    if (state.NodeCount > state.Options.MaxTotalNodes) break;
                }
            }
            catch (Exception ex)
            {
                children.Add(ErrorNode("<enumeration>", ex, state));
            }
            return children;
        }

        private static List<ReplValueNode> BuildDictChildren(
            IDictionary dict, int depth, BuildState state, string parentPath)
        {
            var children = new List<ReplValueNode>();
            int idx = 0;
            try
            {
                foreach (DictionaryEntry e in dict)
                {
                    if (idx >= state.Options.CollectionHeadCount)
                    {
                        int remaining = -1;
                        try { remaining = dict.Count - idx; } catch { /* swallow */ }
                        children.Add(new ReplValueNode
                        {
                            Name = "...",
                            TypeName = "",
                            Preview = remaining >= 0
                                ? $"(remaining {remaining} entries truncated)"
                                : "(remaining entries truncated)",
                            IsExpandable = false,
                            RootValue = state.RootValue,
                        });
                        break;
                    }
                    var keyPreview = ValueFormatter.Format(e.Key);
                    // Try to express the key as C# source so an
                    // Add-Watch on this entry produces a path that
                    // evaluates back to the same bucket. Non-
                    // expressible keys (custom struct keys, control
                    // chars in strings, flag-enum combinations)
                    // surface as a null child path — Add Watch will
                    // grey out for those rows.
                    string encodedKey = TryEncodeDictKeyAsCSharp(e.Key);
                    string childPath = (parentPath == null || encodedKey == null)
                        ? null
                        : parentPath + "[" + encodedKey + "]";
                    children.Add(BuildNode($"[{keyPreview}]", e.Value, depth + 1, state, childPath));
                    idx++;
                    if (state.NodeCount > state.Options.MaxTotalNodes) break;
                }
            }
            catch (Exception ex)
            {
                children.Add(ErrorNode("<enumeration>", ex, state));
            }
            return children;
        }

        private static ReplValueNode ErrorNode(string name, Exception ex, BuildState state) => new ReplValueNode
        {
            Name = name,
            TypeName = "<error>",
            Preview = $"[error: {ex.GetBaseException().Message}]",
            IsExpandable = false,
            // Value + ExpressionPath stay null — error nodes don't
            // have a usable value, and re-asking for the same path
            // would just re-throw the same exception. RootValue is
            // still carried so the stale-tree check stays accurate
            // for the row.
            RootValue = state.RootValue,
        };

        // Render a dictionary key as a C# source-text expression. The
        // result is spliced into a path like `parent[<key>]` that
        // both the normal Roslyn compile and the WatchEvaluator
        // fallback parser need to be able to read — Watch falls back
        // to the parser when compilation fails, and a path the
        // fallback can't re-parse silently stops resolving against
        // the live value. The encoder is therefore narrowed to the
        // exact subset WatchEvaluator.TryReadIndex accepts:
        //
        //   - non-negative `int` (digits only — no signs, no
        //     suffix, no other numeric widths). Long / uint / etc.
        //     reject so the user doesn't see an Add Watch land on
        //     `[123L]` that the fallback parser can't read.
        //   - `string` containing only characters the fallback's
        //     verbatim-quote-to-quote read won't trip on: no
        //     control chars, no embedded `"`, no `\`. The fallback
        //     doesn't interpret escapes, so a path like `["a\"b"]`
        //     would end at the inner quote and never hit the
        //     intended bucket.
        //
        // Bool / char / enum / negative-int / floats / decimals /
        // unsigned / long / and strings needing escapes all return
        // null. The compile path supports more (a Watch row that
        // compiles cleanly will resolve `_.map[true]`), but Add
        // Watch only emits paths both code paths can resolve so
        // users don't see a row light up green only to silently
        // drift later.
        // True iff `name` is a path-safe member identifier under the
        // exact same grammar WatchEvaluator.TryReadIdentifier reads:
        //   first char  ∈ letter ∪ {_}
        //   later chars ∈ letter ∪ digit ∪ {_}
        // Compiler-generated names ("<A>i__Field", "<>1__state",
        // "k__BackingField"), explicit-interface accessors
        // ("IFoo.Bar"), and anything containing punctuation Reflection
        // can produce but the parser can't read fall through to false
        // and the caller marks the row's ExpressionPath unsafe.
        private static bool IsSafeMemberIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            char first = name[0];
            if (!(char.IsLetter(first) || first == '_')) return false;
            for (int i = 1; i < name.Length; i++)
            {
                char c = name[i];
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            }
            return true;
        }

        private static string TryEncodeDictKeyAsCSharp(object key)
        {
            if (key == null) return null;
            switch (key)
            {
                case int i when i >= 0:
                    return i.ToString(CultureInfo.InvariantCulture);
                case string s:
                    foreach (var ch in s)
                    {
                        if (char.IsControl(ch)) return null;
                        if (ch == '"') return null;
                        if (ch == '\\') return null;
                    }
                    return "\"" + s + "\"";
                default:
                    return null;
            }
        }

        // Types treated as atomic leaves — preview is the whole story, no expand.
        private static readonly HashSet<Type> _leafLikeTypes = new HashSet<Type>
        {
            typeof(string), typeof(char), typeof(bool), typeof(decimal),
            typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid),
            typeof(Vector2), typeof(Vector3), typeof(Vector4),
            typeof(Vector2Int), typeof(Vector3Int),
            typeof(Color), typeof(Color32),
            typeof(Quaternion),
            typeof(Rect), typeof(RectInt),
            typeof(Bounds), typeof(BoundsInt),
        };

        private static bool IsUnityFrameworkType(Type t)
        {
            if (t == null) return false;
            var asmName = t.Assembly.GetName().Name;
            if (string.IsNullOrEmpty(asmName)) return false;
            return asmName == "UnityEngine"
                || asmName.StartsWith("UnityEngine.", StringComparison.Ordinal)
                || asmName == "UnityEditor"
                || asmName.StartsWith("UnityEditor.", StringComparison.Ordinal)
                || asmName.StartsWith("Unity.", StringComparison.Ordinal);
        }

        private static bool IsLeafType(Type t)
        {
            if (t.IsPrimitive) return true;
            if (t.IsEnum) return true;
            if (_leafLikeTypes.Contains(t)) return true;
            if (typeof(UnityEngine.Transform).IsAssignableFrom(t)) return true;
            // Treat any Delegate / Action / Func / Event as a leaf. Following
            // their internal _invocationList field walks into every subscribed
            // receiver — typically views and managers across the whole scene —
            // and explodes the graph. Preview shows handler count + first
            // target instead.
            if (typeof(System.Delegate).IsAssignableFrom(t)) return true;
            return false;
        }

        // .NET 5+ has System.Collections.Generic.ReferenceEqualityComparer; we
        // ship our own to stay compatible across Unity Mono runtimes.
        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}

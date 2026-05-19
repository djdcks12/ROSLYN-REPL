using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using RoslynRepl.Editor.Core;

namespace RoslynRepl.Editor.UI
{
    /// <summary>
    /// Issue #63: read-only listing of every session pin currently
    /// stored in <see cref="PinStore"/>, plus per-pin Clear and a
    /// global Clear All. Surfaces the "are my pins still alive?"
    /// question without forcing the user to type a pinned name
    /// into the REPL and watch the result. Session-only contract
    /// is restated in the header so the window itself is the
    /// "clearly marked as session-only" surface the issue calls
    /// for.
    /// </summary>
    public class PinsWindow : EditorWindow
    {
        [MenuItem("Tools/Roslyn REPL/Pins…", priority = 30)]
        public static void Open()
        {
            var w = GetWindow<PinsWindow>(utility: false, title: "Roslyn REPL — Pins", focus: true);
            w.minSize = new Vector2(360, 220);
        }

        private VisualElement _listHost;
        private Label _empty;

        private void OnEnable()
        {
            rootVisualElement.Clear();
            BuildLayout();
            PinStore.Changed -= RefreshList;
            PinStore.Changed += RefreshList;
            RefreshList();
        }

        private void OnDisable()
        {
            PinStore.Changed -= RefreshList;
        }

        private void BuildLayout()
        {
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 4;
            header.style.paddingLeft = 8;
            header.style.paddingRight = 8;
            header.style.paddingTop = 6;

            var title = new Label("Session pins");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.flexGrow = 1;
            header.Add(title);

            var clearAll = new Button(() =>
            {
                if (PinStore.Count == 0) return;
                if (EditorUtility.DisplayDialog(
                        "Clear all pins",
                        $"Drop all {PinStore.Count} session pin(s)? Snippets / watches referencing these names will fail to compile until the pin is restored.",
                        "Clear all",
                        "Cancel"))
                {
                    PinStore.Clear();
                }
            }) { text = "Clear all" };
            header.Add(clearAll);

            rootVisualElement.Add(header);

            // Session-only notice. Pins disappear on domain reload —
            // surface that here so the user doesn't go looking for
            // a "persist pins" toggle that intentionally doesn't
            // exist. Same warm-orange palette the run-warning row
            // uses elsewhere.
            var notice = new Label(
                "Pins live in memory only. A domain reload, an Editor restart, " +
                "or Reset Project Data clears every pin.");
            notice.style.whiteSpace = WhiteSpace.Normal;
            notice.style.color = new StyleColor(new Color(0.86f, 0.71f, 0.31f));
            notice.style.backgroundColor = new StyleColor(new Color(0.86f, 0.71f, 0.31f, 0.10f));
            notice.style.fontSize = 10;
            notice.style.paddingLeft = 8;
            notice.style.paddingRight = 8;
            notice.style.paddingTop = 3;
            notice.style.paddingBottom = 3;
            notice.style.borderBottomWidth = 1;
            notice.style.borderBottomColor = new StyleColor(new Color(0.86f, 0.71f, 0.31f, 0.3f));
            rootVisualElement.Add(notice);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            _listHost = new VisualElement();
            _listHost.style.paddingLeft = 8;
            _listHost.style.paddingRight = 8;
            _listHost.style.paddingTop = 4;
            _listHost.style.paddingBottom = 4;
            scroll.Add(_listHost);
            rootVisualElement.Add(scroll);

            _empty = new Label("(no pins — right-click an Object Browser row or Output tree node → Pin as…)");
            _empty.style.whiteSpace = WhiteSpace.Normal;
            _empty.style.color = new StyleColor(new Color(0.6f, 0.6f, 0.6f));
            _empty.style.unityFontStyleAndWeight = FontStyle.Italic;
            _empty.style.paddingLeft = 8;
            _empty.style.paddingRight = 8;
            _empty.style.paddingTop = 12;
            rootVisualElement.Add(_empty);
        }

        private void RefreshList()
        {
            if (_listHost == null) return;
            _listHost.Clear();
            var snap = PinStore.Snapshot();
            _empty.style.display = snap.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var entry in snap)
            {
                _listHost.Add(BuildRow(entry.Name, entry.Value));
            }
        }

        private VisualElement BuildRow(string name, object value)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingTop = 2;
            row.style.paddingBottom = 2;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = new StyleColor(new Color(0.25f, 0.25f, 0.25f));

            var nameLbl = new Label(name);
            nameLbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLbl.style.minWidth = 110;
            nameLbl.style.maxWidth = 180;
            nameLbl.style.color = new StyleColor(new Color(0.86f, 0.86f, 0.86f));
            row.Add(nameLbl);

            var typeLbl = new Label(value == null ? "null" : TypeFormatter.Short(value.GetType()));
            typeLbl.style.minWidth = 110;
            typeLbl.style.color = new StyleColor(new Color(0.55f, 0.78f, 0.86f));
            typeLbl.style.unityFontStyleAndWeight = FontStyle.Italic;
            typeLbl.style.fontSize = 11;
            row.Add(typeLbl);

            var previewLbl = new Label(SafePreview(value));
            previewLbl.style.flexGrow = 1;
            previewLbl.style.color = new StyleColor(new Color(0.78f, 0.85f, 0.95f));
            previewLbl.style.whiteSpace = WhiteSpace.NoWrap;
            previewLbl.style.overflow = Overflow.Hidden;
            previewLbl.style.textOverflow = TextOverflow.Ellipsis;
            previewLbl.style.fontSize = 11;
            row.Add(previewLbl);

            var capturedName = name;
            var removeBtn = new Button(() => PinStore.Remove(capturedName)) { text = "✕" };
            removeBtn.tooltip = $"Drop pin `{name}`.";
            removeBtn.style.minWidth = 22;
            removeBtn.style.height = 16;
            row.Add(removeBtn);
            return row;
        }

        // Pin values are arbitrary — a ToString that throws shouldn't
        // bring the window down. Pull through the same one-line
        // formatter the REPL elsewhere uses; catch any blow-up and
        // fall back to a tagged placeholder so the row still renders.
        private static string SafePreview(object value)
        {
            try { return ValueFormatter.Format(value); }
            catch (Exception ex) { return $"<ToString threw: {ex.GetBaseException().Message}>"; }
        }
    }
}

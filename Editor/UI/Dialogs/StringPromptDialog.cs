using UnityEditor;
using UnityEngine;

namespace RoslynRepl.Editor.UI.Dialogs
{
    /// <summary>
    /// Tiny modal IMGUI dialog that asks the user for a single
    /// string. Returns the entered value or <c>null</c> if the user
    /// cancelled / Esc'd. Used wherever a string needs to come back
    /// before the caller continues — currently:
    /// <list type="bullet">
    /// <item>Snippet "Save as" (was a private nested class in
    ///       <c>SnippetLibraryWindow</c>; pulled out here so the new
    ///       Pin flow can reuse it without copy-paste).</item>
    /// <item>"Pin as…" from Object Browser / Output tree, where the
    ///       user supplies the pin name.</item>
    /// </list>
    /// <see cref="EditorUtility.DisplayDialog"/> doesn't ship a
    /// string-prompt variant; this is the minimum we need with no
    /// extra dependency.
    /// </summary>
    public class StringPromptDialog : EditorWindow
    {
        private string _label;
        private string _value;
        private bool _confirmed;
        private bool _shouldClose;

        /// <summary>
        /// Show the dialog modally and return the result. Blocks the
        /// calling thread until the user clicks OK / Cancel or
        /// presses Enter / Esc. Returns the entered text on confirm
        /// or <c>null</c> on cancel.
        /// </summary>
        public static string Show(string title, string label, string defaultValue)
        {
            var w = CreateInstance<StringPromptDialog>();
            w.titleContent = new GUIContent(title);
            w._label = label;
            w._value = defaultValue ?? string.Empty;
            w.minSize = new Vector2(320, 90);
            w.maxSize = new Vector2(560, 90);
            w.ShowModal();
            return w._confirmed ? w._value : null;
        }

        private void OnGUI()
        {
            GUILayout.Space(6);
            EditorGUILayout.LabelField(_label);
            GUI.SetNextControlName("input");
            _value = EditorGUILayout.TextField(_value);
            EditorGUI.FocusTextInControl("input");
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(80)))
                {
                    _confirmed = false;
                    _shouldClose = true;
                }
                if (GUILayout.Button("OK", GUILayout.Width(80)))
                {
                    _confirmed = true;
                    _shouldClose = true;
                }
            }
            if (Event.current.type == EventType.KeyDown)
            {
                if (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                {
                    _confirmed = true;
                    _shouldClose = true;
                    Event.current.Use();
                }
                else if (Event.current.keyCode == KeyCode.Escape)
                {
                    _confirmed = false;
                    _shouldClose = true;
                    Event.current.Use();
                }
            }
            if (_shouldClose) Close();
        }
    }
}

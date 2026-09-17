using UnityEditor;
using UnityEngine;

namespace LightSide.LeanBuild
{
    /// <summary>Project-wide switches for what Lean Build keeps out of a player.</summary>
    /// <remarks>Stored in <c>ProjectSettings/LeanBuild.asset</c> and meant to be committed: a build
    /// produces different content depending on it.</remarks>
    [FilePath(SettingsPath, FilePathAttribute.Location.ProjectFolder)]
    internal sealed class LeanBuildSettings : ScriptableSingleton<LeanBuildSettings>
    {
        private const string SettingsPath = "ProjectSettings/LeanBuild.asset";

        private static readonly GUIContent StripLabel = new GUIContent("Strip UI Toolkit from players",
            "Removes the UI Toolkit roots the editor seeds into every player's managed link.");

        [SerializeField] private bool stripUIToolkit;

        /// <summary>Whether players built from this project drop UI Toolkit's managed code.</summary>
        internal static bool StripUIToolkit
        {
            get => instance.stripUIToolkit;
            set
            {
                if (instance.stripUIToolkit == value) return;
                instance.stripUIToolkit = value;
                instance.Save(true);
            }
        }

        [SettingsProvider]
        private static SettingsProvider Provider()
        {
            var provider = new SettingsProvider("Project/Lean Build", SettingsScope.Project)
            {
                guiHandler = _ => Draw(),
                keywords = new[] { "UI Toolkit", "UIElements", "stripping", "build size", "memory" },
            };
            return provider;
        }

        private static void Draw()
        {
            EditorGUILayout.Space();
            EditorGUI.indentLevel++;
            StripUIToolkit = EditorGUILayout.Toggle(StripLabel, StripUIToolkit);
            if (StripUIToolkit)
                EditorGUILayout.HelpBox(
                    "Players will not run UI Toolkit. Anything that needs it at run time stops working, " +
                    "including a UIDocument placed in a scene, which the compiler cannot warn about. " +
                    "The editor is unaffected.", MessageType.Warning);
            EditorGUI.indentLevel--;
        }
    }
}

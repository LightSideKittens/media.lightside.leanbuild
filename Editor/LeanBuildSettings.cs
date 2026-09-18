using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace LightSide.LeanBuild
{
    /// <summary>Project-wide switches for what Lean Build keeps out of a player.</summary>
    /// <remarks>Stored in <c>ProjectSettings/LeanBuild.asset</c> and meant to be committed: a build
    /// produces different content depending on it.</remarks>
    [FilePath(SettingsPath, FilePathAttribute.Location.ProjectFolder)]
    internal sealed class LeanBuildSettings : ScriptableSingleton<LeanBuildSettings>
    {
        private const string SettingsPath = "ProjectSettings/LeanBuild.asset";
        private const string SettingsMenuPath = "Project/LightSide/Lean Build";
        private const string StyleSheetName = "LightSideLeanBuild";

        [SerializeField] private bool stripUIToolkit;

        private static StyleSheet styleSheet;

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
        private static SettingsProvider Provider() =>
            new SettingsProvider(SettingsMenuPath, SettingsScope.Project)
            {
                label = "Lean Build",
                keywords = new[] { "UI Toolkit", "UIElements", "stripping", "build size", "memory" },
                activateHandler = (_, root) => Build(root),
            };

        private static void Build(VisualElement root)
        {
            var page = new ScrollView(ScrollViewMode.Vertical);
            page.AddToClassList("leanbuild");
            page.EnableInClassList("leanbuild--dark", EditorGUIUtility.isProSkin);
            page.EnableInClassList("leanbuild--light", !EditorGUIUtility.isProSkin);
            page.contentContainer.AddToClassList("leanbuild__page");
            page.styleSheets.Add(Style());
            root.Add(page);

            var card = new VisualElement();
            card.AddToClassList("leanbuild__card");
            page.Add(card);

            card.Add(Text("Lean Build", "leanbuild__title"));
            card.Add(Text(
                "Unity seeds its own editor's UI Toolkit types into every player's managed link roots, " +
                "so the UIElements runtime is compiled into builds that cannot use it. Removing the " +
                "package or guarding every reference does not take it out; cutting those roots and " +
                "holding the module out of the player's compilation does.",
                "leanbuild__lead"));

            var note = Text(
                "Builds fail until no player code reaches UI Toolkit. The module is held out of the " +
                "player's compilation, so every use of it becomes a compiler error naming its file and " +
                "line — fix each where it lives, then drop the fixed files below to keep the fix. The " +
                "Editor is unaffected.\n\n" +
                "Once a build passes, players no longer run UI Toolkit. Anything that needs it at run " +
                "time stops working, including a UIDocument placed in a scene, which the compiler cannot " +
                "warn about.",
                "leanbuild__note");
            note.style.display = StripUIToolkit ? DisplayStyle.Flex : DisplayStyle.None;

            var strip = Checkbox("Strip UI Toolkit from players");
            strip.value = StripUIToolkit;
            strip.RegisterValueChangedCallback(changed =>
            {
                StripUIToolkit = changed.newValue;
                note.style.display = changed.newValue ? DisplayStyle.Flex : DisplayStyle.None;
            });
            card.Add(strip);
            card.Add(note);

            PatchSection.Build(page);
        }

        /// <summary>A checkbox drawn the LightSide way: the engine's tick replaced by two accent strokes.</summary>
        private static Toggle Checkbox(string text)
        {
            var toggle = new Toggle { text = text };
            toggle.AddToClassList("leanbuild__toggle");
            Tag(toggle, Toggle.inputUssClassName, "leanbuild__input");
            Tag(toggle, Toggle.textUssClassName, "leanbuild__text");

            var checkmark = toggle.Q<VisualElement>(className: Toggle.checkmarkUssClassName)
                            ?? throw new InvalidOperationException("The toggle has no checkmark element.");
            checkmark.AddToClassList("leanbuild__checkmark");
            var mark = new VisualElement { pickingMode = PickingMode.Ignore };
            mark.AddToClassList("leanbuild__mark");
            mark.Add(Stroke("leanbuild__mark-short"));
            mark.Add(Stroke("leanbuild__mark-long"));
            checkmark.Add(mark);
            return toggle;
        }

        /// <summary>Names one of the engine's own parts so the stylesheet never spells a Unity class.</summary>
        private static void Tag(VisualElement root, string engineClass, string className)
            => root.Q<VisualElement>(className: engineClass)?.AddToClassList(className);

        private static VisualElement Stroke(string className)
        {
            var stroke = new VisualElement { pickingMode = PickingMode.Ignore };
            stroke.AddToClassList(className);
            return stroke;
        }

        private static Label Text(string text, string className)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            label.AddToClassList(className);
            return label;
        }

        /// <exception cref="InvalidOperationException">The stylesheet is missing from the package.</exception>
        private static StyleSheet Style() =>
            styleSheet ??= Resources.Load<StyleSheet>(StyleSheetName)
                           ?? throw new InvalidOperationException(
                               $"The Lean Build stylesheet '{StyleSheetName}' is missing from the package.");
    }
}

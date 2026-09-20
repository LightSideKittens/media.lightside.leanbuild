using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace LightSide.Lean
{
    /// <summary>Project-wide switches for what Lean keeps out of a player.</summary>
    /// <remarks>Stored in <c>ProjectSettings/LightSideLean.asset</c> and meant to be committed: a build
    /// produces different content depending on it.</remarks>
    [FilePath(LeanPaths.Settings, FilePathAttribute.Location.ProjectFolder)]
    internal sealed class LeanSettings : ScriptableSingleton<LeanSettings>
    {
        private const string SettingsMenuPath = "Project/LightSide/Lean";
        private const string StyleSheetName = "LightSideLean";

        [SerializeField] private bool stripUIToolkit;

        private static StyleSheet styleSheet;

        /// <summary>Whether players built from this project drop UI Toolkit's managed code.</summary>
        internal static bool StripUIToolkit
        {
            get => Current.stripUIToolkit;
            set
            {
                if (Current.stripUIToolkit == value) return;
                Current.stripUIToolkit = value;
                Current.Save(true);
            }
        }

        /// <summary>The settings, with anything left under the package's former names taken over first.</summary>
        /// <remarks>Reading before the move would load a fresh instance with every value at its default
        /// and leave the developer's own switch behind, so the move happens on the way in rather than from
        /// a load callback whose order against this read is undefined.</remarks>
        private static LeanSettings Current
        {
            get
            {
                LeanPaths.Adopt();
                return instance;
            }
        }

        [SettingsProvider]
        private static SettingsProvider Provider() =>
            new SettingsProvider(SettingsMenuPath, SettingsScope.Project)
            {
                label = "Lean",
                keywords = new[]
                {
                    "UI Toolkit", "UIElements", "stripping", "build size", "memory", "patch", "package",
                },
                activateHandler = (_, root) => Build(root),
            };

        private static void Build(VisualElement root)
        {
            var page = new ScrollView(ScrollViewMode.Vertical);
            page.AddToClassList("lean");
            page.EnableInClassList("lean--dark", EditorGUIUtility.isProSkin);
            page.EnableInClassList("lean--light", !EditorGUIUtility.isProSkin);
            page.contentContainer.AddToClassList("lean__page");
            page.styleSheets.Add(Style());
            root.Add(page);

            var card = new VisualElement();
            card.AddToClassList("lean__card");
            page.Add(card);

            card.Add(Text("Strip UI Toolkit", "lean__title"));
            card.Add(Text(
                "Unity seeds its own editor's UI Toolkit types into every player's managed link roots, " +
                "so the UIElements runtime is compiled into builds that cannot use it. Removing the " +
                "package or guarding every reference does not take it out; cutting those roots and " +
                "holding the module out of the player's compilation does.",
                "lean__lead"));

            var note = Text(
                "Builds fail until no player code reaches UI Toolkit. The module is held out of the " +
                "player's compilation, so every use of it becomes a compiler error naming its file and " +
                "line — fix each where it lives, then drop the fixed files below to keep the fix. The " +
                "Editor is unaffected.\n\n" +
                "Once a build passes, players no longer run UI Toolkit. Anything that needs it at run " +
                "time stops working, including a UIDocument placed in a scene, which the compiler cannot " +
                "warn about.",
                "lean__note");
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

            PatchSection.Build(page, PatchStore.Build);
            PatchSection.Build(page, PatchStore.Permanent);
        }

        /// <summary>A checkbox drawn the LightSide way: the engine's tick replaced by two accent strokes.</summary>
        private static Toggle Checkbox(string text)
        {
            var toggle = new Toggle { text = text };
            toggle.AddToClassList("lean__toggle");
            Tag(toggle, Toggle.inputUssClassName, "lean__input");
            Tag(toggle, Toggle.textUssClassName, "lean__text");

            var checkmark = toggle.Q<VisualElement>(className: Toggle.checkmarkUssClassName)
                            ?? throw new InvalidOperationException("The toggle has no checkmark element.");
            checkmark.AddToClassList("lean__checkmark");
            var mark = new VisualElement { pickingMode = PickingMode.Ignore };
            mark.AddToClassList("lean__mark");
            mark.Add(Stroke("lean__mark-short"));
            mark.Add(Stroke("lean__mark-long"));
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
                               $"The Lean stylesheet '{StyleSheetName}' is missing from the package.");
    }
}

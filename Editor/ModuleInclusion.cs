using System;
using System.Reflection;

namespace LightSide.LeanBuild
{
    /// <summary>Whether a build includes an engine module, independently of what references it.</summary>
    /// <remarks>Values mirror <c>UnityEditor.ModuleIncludeSetting</c> and are written to the build as-is.</remarks>
    internal enum ModuleInclusionState
    {
        /// <summary>Unity decides from the project's packages and references.</summary>
        Auto = 0,
        /// <summary>The module is kept out of the player even when a package declares it.</summary>
        ForceExclude = 1,
        /// <summary>The module is added to the player even when nothing references it.</summary>
        ForceInclude = 2,
    }

    /// <summary>
    /// Reads and writes Unity's per-module inclusion setting, the switch that decides whether an engine
    /// module reaches the player.
    /// </summary>
    /// <remarks>
    /// Backed by <c>UnityEditor.ModuleMetadata</c>, which is internal to the Editor assembly, so the
    /// binding is resolved by reflection and can be absent on an Editor version that renames or moves it;
    /// check <see cref="Supported"/> before every call. The value lives for the editor session only —
    /// measured on 6000.6, where a fresh editor reports <see cref="ModuleInclusionState.Auto"/> again and
    /// nothing is written to <c>ProjectSettings</c> — so a build that dies before restoring it costs at
    /// most the rest of that session.
    /// </remarks>
    internal static class ModuleInclusion
    {
        private const string MetadataType = "UnityEditor.ModuleMetadata";
        private const string SettingType = "UnityEditor.ModuleIncludeSetting";

        private static readonly MethodInfo Getter;
        private static readonly MethodInfo Setter;
        private static readonly MethodInfo Strippable;
        private static readonly Type Setting;

        static ModuleInclusion()
        {
            var editor = typeof(UnityEditor.Editor).Assembly;
            var metadata = editor.GetType(MetadataType, false);
            Setting = editor.GetType(SettingType, false);
            if (metadata == null || Setting == null || !Setting.IsEnum) return;

            Getter = metadata.GetMethod("GetModuleIncludeSettingForModule",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            Setter = metadata.GetMethod("SetModuleIncludeSettingForModule",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), Setting }, null);
            Strippable = metadata.GetMethod("IsStrippableModule",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
        }

        /// <summary>Whether this editor exposes the inclusion setting at all.</summary>
        internal static bool Supported => Getter != null && Setter != null;

        /// <summary>Whether <paramref name="module"/> may be kept out of a player on this editor.</summary>
        internal static bool IsStrippable(string module) =>
            Strippable != null && (bool)Strippable.Invoke(null, new object[] { module });

        /// <summary>Current inclusion setting of <paramref name="module"/>, by its engine module name.</summary>
        /// <exception cref="NotSupportedException">This editor version does not expose the setting.</exception>
        internal static ModuleInclusionState Get(string module)
        {
            Require();
            return (ModuleInclusionState)(int)Getter.Invoke(null, new object[] { module });
        }

        /// <summary>Writes the inclusion setting of <paramref name="module"/> for this editor session.</summary>
        /// <exception cref="NotSupportedException">This editor version does not expose the setting.</exception>
        internal static void Set(string module, ModuleInclusionState state)
        {
            Require();
            Setter.Invoke(null, new[] { module, Enum.ToObject(Setting, (int)state) });
        }

        private static void Require()
        {
            if (!Supported)
                throw new NotSupportedException(
                    $"{MetadataType} / {SettingType} are not reachable in {UnityEngine.Application.unityVersion}.");
        }
    }
}

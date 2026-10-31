using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;

namespace LightSide.Lean
{
    /// <summary>
    /// Keeps the scripting define that tells LightSide packages to compile their UI Toolkit code out in step
    /// with <see cref="LeanSettings.StripUIToolkit"/>, on every build target.
    /// </summary>
    /// <remarks>
    /// The define lives in Player Settings because the build compiles runtime assemblies three times and only a
    /// Player Settings define reaches all three passes. Turning the setting off removes it; opening a project
    /// with the setting on adds it where it is missing, so a checkout that arrives without it is repaired. With
    /// the setting off an opened project is left alone, so a define set by hand keeps working.
    /// </remarks>
    internal static class PackagesDefine
    {
        internal const string Symbol = "LIGHTSIDE_NO_UITOOLKIT";

        private static readonly NamedBuildTarget[] Targets = Enum.GetValues(typeof(BuildTargetGroup))
            .Cast<BuildTargetGroup>()
            .Where(group => group != BuildTargetGroup.Unknown
                            && typeof(BuildTargetGroup).GetField(group.ToString())
                                .GetCustomAttributes(typeof(ObsoleteAttribute), false).Length == 0)
            .Select(NamedBuildTarget.FromBuildTargetGroup)
            .Distinct()
            .ToArray();

        [InitializeOnLoadMethod]
        private static void Reconcile()
        {
            if (LeanSettings.StripUIToolkit) EditorApplication.delayCall += () => Apply(true);
        }

        /// <summary>Adds or removes <see cref="Symbol"/> on every target that does not already match.</summary>
        internal static void Apply(bool present)
        {
            foreach (var target in Targets)
            {
                var defines = PlayerSettings.GetScriptingDefineSymbols(target)
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (defines.Contains(Symbol) == present) continue;
                if (present) defines.Add(Symbol);
                else defines.RemoveAll(define => define == Symbol);
                PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", defines));
            }
        }
    }
}

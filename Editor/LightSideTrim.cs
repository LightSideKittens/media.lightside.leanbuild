using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEngine;

namespace LightSide.Lean
{
    /// <summary>
    /// Strips LightSide's assemblies as Medium does in an IL2CPP player that stays on Minimal, while
    /// <see cref="LeanSettings.TrimLightSide"/> is on, and keeps every other compiled assembly whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At Minimal an assembly a scene references ships whole, and no per-assembly linker action changes that: the
    /// rule set decides. So the link runs under Medium's rule set, and each compiled assembly that is not trimmed
    /// gets the copy action, which is what Minimal gives it. .NET and engine assemblies follow the rule set.
    /// </para>
    /// <para>
    /// The linker takes these arguments from <c>UNITYLINKER_ADDITIONAL_ARGS</c>, which Unity reads but does not
    /// document. An editor that stops reading it builds at full size, and <see cref="OnPostprocessBuild"/> says so.
    /// </para>
    /// <para>
    /// Unity reads the variable only while generating the player's build graph, and nothing tells it to generate
    /// the graph again when the variable changes. So a change of arguments deletes the graph, whichever way the
    /// setting moved; only that build pays for it.
    /// </para>
    /// </remarks>
    internal sealed class LightSideTrim : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        private const string Variable = "UNITYLINKER_ADDITIONAL_ARGS";
        private const string RuleSet = "--rule-set=Aggressive";
        private const string Prefix = "LightSide.";
        private const string Dependency = "Unity.Burst";
        private const string BeeDirectory = "Library/Bee";
        private const string GraphFiles = "Player*.dag*";
        private const string SourceDirectory = BeeDirectory + "/PlayerScriptAssemblies";
        private const string ArtifactsDirectory = BeeDirectory + "/artifacts";
        private const string StrippedDirectory = "ManagedStripped";
        private const string LastArguments = LeanPaths.Work + "/trim-arguments.txt";

        private static bool applied;
        private static string previous;

        /// <inheritdoc/>
        public int callbackOrder => 0;

        /// <inheritdoc/>
        public void OnPreprocessBuild(BuildReport report)
        {
            Restore();
            var arguments = Arguments(report);
            ForgetGraphOnChange(arguments);
            if (arguments.Length == 0) return;

            previous = Environment.GetEnvironmentVariable(Variable);
            Environment.SetEnvironmentVariable(Variable, string.IsNullOrEmpty(previous)
                ? arguments
                : previous + " " + arguments);
            applied = true;
            EditorApplication.delayCall += Restore;
        }

        /// <inheritdoc/>
        public void OnPostprocessBuild(BuildReport report)
        {
            if (applied) Report();
            Restore();
        }

        private static string Arguments(BuildReport report)
        {
            if (!LeanSettings.TrimLightSide) return string.Empty;
            var target = NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup);
            if (PlayerSettings.GetScriptingBackend(target) != ScriptingImplementation.IL2CPP) return string.Empty;
            if (PlayerSettings.GetManagedStrippingLevel(target) != ManagedStrippingLevel.Minimal) return string.Empty;

            var kept = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies)
                .Select(assembly => assembly.name)
                .Concat(CompilationPipeline
                    .GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.UserAssembly)
                    .Select(Path.GetFileNameWithoutExtension))
                .Where(name => !name.StartsWith(Prefix, StringComparison.Ordinal) &&
                               !name.StartsWith(Dependency, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => "Copy," + name);
            return RuleSet + " --additional-action=" + string.Join(",", kept);
        }

        private static void ForgetGraphOnChange(string arguments)
        {
            var last = File.Exists(LastArguments) ? File.ReadAllText(LastArguments) : string.Empty;
            if (last == arguments) return;

            if (Directory.Exists(BeeDirectory))
                foreach (var file in Directory.GetFiles(BeeDirectory, GraphFiles))
                    File.Delete(file);
            Directory.CreateDirectory(LeanPaths.Work);
            File.WriteAllText(LastArguments, arguments);
        }

        private static void Restore()
        {
            if (!applied) return;
            Environment.SetEnvironmentVariable(Variable, previous);
            applied = false;
        }

        private static void Report()
        {
            var stripped = Directory.Exists(ArtifactsDirectory)
                ? Directory.GetDirectories(ArtifactsDirectory, StrippedDirectory, SearchOption.AllDirectories)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            if (stripped == null)
            {
                Debug.LogWarning("[Lean] This build wrote no stripped assemblies where Lean looks, so whether " +
                                 "LightSide's assemblies were trimmed cannot be confirmed here.");
                return;
            }

            long before = 0, after = 0;
            foreach (var path in Directory.GetFiles(stripped, Prefix + "*.dll"))
            {
                var source = Path.Combine(SourceDirectory, Path.GetFileName(path));
                if (!File.Exists(source)) continue;
                before += new FileInfo(source).Length;
                after += new FileInfo(path).Length;
            }

            if (after > 0 && after < before * 9 / 10)
                Debug.Log($"[Lean] LightSide's assemblies trimmed: {before / 1048576f:F2} → {after / 1048576f:F2} MiB of IL.");
            else
                Debug.LogWarning($"[Lean] LightSide's assemblies were not trimmed: {before / 1048576f:F2} → " +
                                 $"{after / 1048576f:F2} MiB of IL. This editor may no longer read {Variable}.");
        }
    }
}

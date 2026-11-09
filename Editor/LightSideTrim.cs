using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.UnityLinker;
using UnityEngine;

namespace LightSide.Lean
{
    /// <summary>
    /// Strips LightSide's assemblies as Medium does in an IL2CPP player that stays on Minimal or Low, while
    /// <see cref="LeanSettings.TrimLightSide"/> is on, and leaves every other compiled assembly as that level does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At Minimal and Low an assembly a scene references ships whole, and no per-assembly linker action changes that:
    /// the rule set decides. So the link runs under Medium's rule set, and the other assemblies get back what their
    /// level gives them. At Minimal that is the copy action for each of them.
    /// </para>
    /// <para>
    /// At Low it is whole for the assemblies Unity passes as roots and Medium's treatment for the rest, which Low
    /// shares. The roots are the compiled assemblies not defined under <c>Packages</c>, and the assemblies named in
    /// <c>TypesInScenes.xml</c>, which exists only once the build has collected its scenes. So
    /// <see cref="GenerateAdditionalLinkXmlFile"/> adds them to the link XML, and a build without that file fails.
    /// </para>
    /// <para>
    /// Medium's rule set also drops the preservation lists Unity applies to .NET at Minimal and Low, so the link reads
    /// the lists an IL2CPP player uses from the running editor's il2cpp folder, which macOS editors keep under
    /// <c>Contents/Resources/Scripting</c> from Unity 6.3. A missing list fails the build, because the members it
    /// protects would otherwise go missing only at run time. Engine assemblies follow Medium's rule set.
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
    internal sealed class LightSideTrim : IPreprocessBuildWithReport, IPostprocessBuildWithReport, IUnityLinkerProcessor
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
        private const string LinkXml = LeanPaths.Work + "/trim.link.xml";
        private const string ScriptingDirectory = "Resources/Scripting";
        private const string DescriptorDirectory = "il2cpp/LinkerDescriptors/Default";
        private const string TypesInScenes = "TypesInScenes.xml";
        private const string PackagesDirectory = "Packages/";

        private static readonly string[] Descriptors =
        {
            "mscorlib.xml", "System.xml", "System.Configuration.xml", "System.Net.xml", "45/mscorlib.xml", "45/System.xml"
        };

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

            WriteLinkXml();
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

        /// <inheritdoc/>
        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(data.target));
            if (!applied || PlayerSettings.GetManagedStrippingLevel(target) != ManagedStrippingLevel.Low) return null;

            var document = XDocument.Load(LinkXml);
            foreach (var name in RootsAtLow(data))
                document.Root.Add(new XElement("assembly", new XAttribute("fullname", name),
                    new XAttribute("preserve", "all"), new XAttribute("ignoreIfMissing", "1")));
            document.Save(LinkXml);
            return null;
        }

        private static string Arguments(BuildReport report)
        {
            if (!LeanSettings.TrimLightSide) return string.Empty;
            var target = NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup);
            if (PlayerSettings.GetScriptingBackend(target) != ScriptingImplementation.IL2CPP) return string.Empty;

            var arguments = RuleSet + " --include-link-xml=" + LinkXml;
            switch (PlayerSettings.GetManagedStrippingLevel(target))
            {
                case ManagedStrippingLevel.Low:
                    return arguments;
                case ManagedStrippingLevel.Minimal:
                    var kept = Compiled().Concat(Precompiled())
                        .Where(IsKept)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .Select(name => "Copy," + name);
                    return arguments + " --additional-action=" + string.Join(",", kept);
                default:
                    return string.Empty;
            }
        }

        private static IEnumerable<string> RootsAtLow(UnityLinkerBuildPipelineData data)
        {
#pragma warning disable CS0618
            var directories = new[] { data.inputDirectory, LinkerRootTrimmer.InputsDirectory };
#pragma warning restore CS0618
            var file = directories.Select(directory => Path.Combine(directory, TypesInScenes)).FirstOrDefault(File.Exists);
            if (file == null)
                throw new BuildFailedException(
                    $"[Lean] Trim LightSide Code keeps your assemblies whole from {TypesInScenes}, and this editor " +
                    "did not write it where Lean looks. Turn the setting off or update Lean.");

            var inScenes = XDocument.Load(file).Root.Elements("assembly")
                .Select(assembly => (string)assembly.Attribute("fullname"))
                .ToHashSet(StringComparer.Ordinal);
            return Compiled()
                .Where(name => CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(name)
                    ?.StartsWith(PackagesDirectory, StringComparison.Ordinal) != true)
                .Concat(Compiled().Concat(Precompiled()).Where(inScenes.Contains))
                .Where(IsKept)
                .Distinct(StringComparer.Ordinal);
        }

        private static IEnumerable<string> Compiled() =>
            CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies).Select(assembly => assembly.name);

        private static IEnumerable<string> Precompiled() =>
            CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.UserAssembly)
                .Select(Path.GetFileNameWithoutExtension);

        private static bool IsKept(string name) =>
            !name.StartsWith(Prefix, StringComparison.Ordinal) && !name.StartsWith(Dependency, StringComparison.Ordinal);

        private static void WriteLinkXml()
        {
            var contents = EditorApplication.applicationContentsPath;
            var scripting = Path.Combine(contents, ScriptingDirectory);
            var descriptors = Path.Combine(Directory.Exists(scripting) ? scripting : contents, DescriptorDirectory);
            var linker = new XElement("linker");
            foreach (var name in Descriptors)
            {
                var path = Path.Combine(descriptors, name);
                if (!File.Exists(path))
                    throw new BuildFailedException(
                        "[Lean] Trim LightSide Code keeps .NET members through Unity's preservation lists, and this " +
                        $"editor has no '{path}'. Turn the setting off or update Lean.");

                foreach (var assembly in XDocument.Load(path).Root.Elements("assembly"))
                {
                    assembly.SetAttributeValue("ignoreIfMissing", "1");
                    linker.Add(assembly);
                }
            }
            new XDocument(linker).Save(LinkXml);
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

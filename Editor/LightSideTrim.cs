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
using UnityEditorInternal;
using UnityEngine;

namespace LightSide.Lean
{
    /// <summary>
    /// Strips LightSide's assemblies as Medium does in an IL2CPP player that stays on Minimal or Low, while
    /// <see cref="LeanSettings.TrimLightSide"/> is on, and leaves every other assembly as that level does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At Minimal and Low an assembly a scene references ships whole, and no per-assembly linker action changes that:
    /// the rule set decides. So the link runs under Medium's rule set, and a link XML gives every other assembly back
    /// what its level gives it.
    /// </para>
    /// <para>
    /// Both levels keep their roots whole: the compiled assemblies not defined under <c>Packages</c>, and the ones
    /// named in <c>TypesInScenes.xml</c>, which exists only once the build has collected its scenes, so
    /// <see cref="GenerateAdditionalLinkXmlFile"/> adds those and a build without that file fails. The project's
    /// assemblies come from its assembly definitions and plugins, not from the editor's compilation, which leaves out
    /// an assembly whose define a build script sets only for the build; the linker ignores a name it does not load.
    /// Minimal also copies every other assembly the linker loads that is neither .NET, an engine module nor the
    /// <c>UnityEngine</c> facade, so there the link copies by default and links the engine modules, recognized by their
    /// assembly attribute as the linker recognizes them, the facade and LightSide's assemblies. The linker links
    /// every engine assembly the facade references, so the engine's other assemblies are kept whole at Minimal.
    /// </para>
    /// <para>
    /// Medium's rule set keeps only the methods with a body of each MonoBehaviour, its subclasses and the subclasses of
    /// ScriptableObject it keeps, and Minimal and Low keep them all, so the link XML keeps every method of such a type
    /// whenever the linker keeps the type. Medium's rule set also drops the preservation lists Unity applies to .NET at
    /// Minimal and Low, so the link reads the lists an IL2CPP player uses from the running editor's il2cpp folder,
    /// which macOS editors keep under <c>Contents/Resources/Scripting</c> from Unity 6.3. A missing list fails the
    /// build, because the members it protects would otherwise go missing only at run time.
    /// </para>
    /// <para>
    /// The linker takes these arguments from <c>UNITYLINKER_ADDITIONAL_ARGS</c>, which Unity reads but does not
    /// document. An editor that stops reading it builds at full size, and <see cref="OnPostprocessBuild"/> says so.
    /// </para>
    /// <para>
    /// Unity reads the variable only while generating the player's build graph, and nothing tells it to generate
    /// the graph again when the variable changes. So a change of arguments deletes the graph, whichever way the
    /// setting or the level moved; only that build pays for it.
    /// </para>
    /// </remarks>
    internal sealed class LightSideTrim : IPreprocessBuildWithReport, IPostprocessBuildWithReport, IUnityLinkerProcessor
    {
        private const string Variable = "UNITYLINKER_ADDITIONAL_ARGS";
        private const string RuleSet = "--rule-set=Aggressive --include-link-xml=" + LinkXml;
        private const string CopyOthers = " --user-action=copy --additional-action=";
        private const string Prefix = "LightSide.";
        private const string Dependency = "Unity.Burst";
        private const string EditorPrefix = "UnityEditor";
        private const string EngineFacade = "UnityEngine";
        private const string EngineModuleAttribute = "UnityEngine.UnityEngineModuleAssembly";
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

        [Serializable]
        private struct Definition
        {
            public string name;
        }

        /// <inheritdoc/>
        public int callbackOrder => 0;

        /// <inheritdoc/>
        public void OnPreprocessBuild(BuildReport report)
        {
            Restore();
            var level = TrimmedLevel(NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup));
            var arguments = Arguments(level);
            ForgetGraphOnChange(arguments);
            if (level == null) return;

            WriteLinkXml(level.Value);
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
            if (!applied) return null;

            var document = XDocument.Load(LinkXml);
            foreach (var name in UsedInScenes(data)) document.Root.Add(Whole(name));
            document.Save(LinkXml);
            return null;
        }

        private static ManagedStrippingLevel? TrimmedLevel(NamedBuildTarget target)
        {
            if (!LeanSettings.TrimLightSide) return null;
            if (PlayerSettings.GetScriptingBackend(target) != ScriptingImplementation.IL2CPP) return null;
            var level = PlayerSettings.GetManagedStrippingLevel(target);
            return level is ManagedStrippingLevel.Minimal or ManagedStrippingLevel.Low ? level : null;
        }

        private static string Arguments(ManagedStrippingLevel? level)
        {
            switch (level)
            {
                case ManagedStrippingLevel.Low:
                    return RuleSet;
                case ManagedStrippingLevel.Minimal:
                    var linked = EngineAssemblies().Where(engine => engine.Value).Select(engine => engine.Key)
                        .Concat(ProjectAssemblies().Keys.Where(name => !IsKept(name)))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .Select(name => "Link," + name);
                    return RuleSet + CopyOthers + string.Join(",", linked);
                default:
                    return string.Empty;
            }
        }

        private static Dictionary<string, bool> EngineAssemblies()
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .ToLookup(assembly => assembly.GetName().Name, StringComparer.Ordinal);
            return CompilationPipeline.GetPrecompiledAssemblyPaths(CompilationPipeline.PrecompiledAssemblySources.UnityEngine)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !name.StartsWith(EditorPrefix, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(name => name, name => name == EngineFacade || loaded[name].Any(IsEngineModule),
                    StringComparer.Ordinal);
        }

        private static bool IsEngineModule(System.Reflection.Assembly assembly) =>
            assembly.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == EngineModuleAttribute);

        private static IEnumerable<string> UsedInScenes(UnityLinkerBuildPipelineData data)
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
            return ProjectAssemblies()
                .Where(assembly => !assembly.Value && inScenes.Contains(assembly.Key) && IsKept(assembly.Key))
                .Select(assembly => assembly.Key);
        }

        private static Dictionary<string, bool> ProjectAssemblies()
        {
            var assemblies = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var plugin in PluginImporter.GetAllImporters())
                if (!plugin.isNativePlugin)
                    assemblies[Path.GetFileNameWithoutExtension(plugin.assetPath)] = false;
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
                assemblies[assembly.name] =
                    OutsidePackages(CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name));
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(AssemblyDefinitionAsset)))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var definition = AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(path);
                assemblies[JsonUtility.FromJson<Definition>(definition.text).name] = OutsidePackages(path);
            }
            return assemblies;
        }

        private static bool OutsidePackages(string path) =>
            path?.StartsWith(PackagesDirectory, StringComparison.Ordinal) != true;

        private static bool IsKept(string name) =>
            !name.StartsWith(Prefix, StringComparison.Ordinal) && !name.StartsWith(Dependency, StringComparison.Ordinal);

        private static XElement Whole(string name) =>
            new XElement("assembly", new XAttribute("fullname", name), new XAttribute("preserve", "all"),
                new XAttribute("ignoreIfMissing", "1"), new XAttribute("ignoreIfUnreferenced", "1"));

        private static void WriteLinkXml(ManagedStrippingLevel level)
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

            var whole = ProjectAssemblies()
                .Where(assembly => assembly.Value && IsKept(assembly.Key))
                .Select(assembly => assembly.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (level == ManagedStrippingLevel.Minimal)
                whole.UnionWith(EngineAssemblies().Where(engine => !engine.Value).Select(engine => engine.Key));
            foreach (var name in whole.OrderBy(name => name, StringComparer.Ordinal)) linker.Add(Whole(name));

            var components = TypeCache.GetTypesDerivedFrom<MonoBehaviour>().Append(typeof(MonoBehaviour))
                .Concat(TypeCache.GetTypesDerivedFrom<ScriptableObject>())
                .Where(type =>
                {
                    var assembly = type.Assembly.GetName().Name;
                    return IsKept(assembly) && !whole.Contains(assembly) &&
                           !assembly.StartsWith(EditorPrefix, StringComparison.Ordinal);
                })
                .GroupBy(type => type.Assembly.GetName().Name)
                .OrderBy(group => group.Key, StringComparer.Ordinal);
            foreach (var types in components)
                linker.Add(new XElement("assembly", new XAttribute("fullname", types.Key),
                    new XAttribute("ignoreIfMissing", "1"), new XAttribute("ignoreIfUnreferenced", "1"),
                    types.Select(type => type.FullName.Replace('+', '/'))
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .Select(name => new XElement("type", new XAttribute("fullname", name),
                            new XAttribute("preserve", "methods"), new XAttribute("required", "false")))));

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

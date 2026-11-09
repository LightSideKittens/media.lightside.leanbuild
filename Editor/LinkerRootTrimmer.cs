using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
using UnityEngine;

namespace LightSide.Lean
{
    /// <summary>
    /// Takes UI Toolkit out of the player while <see cref="LeanSettings.StripUIToolkit"/> is on: the
    /// roots the editor seeds into the managed link, and the engine module itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// UnityLinker takes its roots from the link XML that
    /// <c>UnityEditorInternal.AssemblyStripper.GetLinkXmlFiles</c> writes — into
    /// <c>Library/Bee/artifacts/UnityLinkerInputs</c> on Unity 6, into the folder it passes as
    /// <see cref="UnityLinkerBuildPipelineData"/>'s input directory on 2022.3 — and the type lists behind
    /// those files come from the editor's <c>RuntimeClassRegistry</c>, which names every type the build's
    /// content instances. A UI Toolkit asset the build carries, such as a <c>PanelSettings</c> in a
    /// <c>Resources</c> folder, therefore roots <c>PanelSettings</c> and <c>DynamicAtlasSettings</c> in a
    /// player that never draws with them, and those two roots keep <c>UnityEngine.UIElementsModule</c>
    /// whole and pull members out of a dozen more assemblies.
    /// </para>
    /// <para>
    /// The trim runs from <see cref="GenerateAdditionalLinkXmlFile"/> because that callback is the only
    /// public point between those files being written and UnityLinker reading them. An editor that
    /// reorders the two, or writes the files somewhere else, keeps the module, and
    /// <see cref="OnPostprocessBuild"/> says so.
    /// </para>
    /// <para>
    /// Cutting the roots only helps a player whose own code never reaches UI Toolkit; anything the linker
    /// can still walk to it keeps the module regardless. <see cref="UIToolkitDefines"/> compiles out what a
    /// package gates behind its assembly definition, such as uGUI's panel bridge, and the module is also held
    /// out of the player's compilation, which turns every other reference into a compiler error rather than a
    /// build that quietly saves nothing. Where an editor does not expose module inclusion that lever is
    /// unavailable, and <see cref="OnPostprocessBuild"/> reports that the module stayed.
    /// </para>
    /// </remarks>
    internal sealed class LinkerRootTrimmer : IUnityLinkerProcessor, IPreprocessBuildWithReport,
        IPostprocessBuildWithReport
    {
        private const string Module = "UnityEngine.UIElementsModule";
        private const string EngineModule = "UIElements";
        internal const string InputsDirectory = "Library/Bee/artifacts/UnityLinkerInputs";
        private const string OwnFile = "Lean.link.xml";

        private static readonly string[] Inputs = { "TypesInScenes.xml", "SerializedTypes.xml" };

        /// <inheritdoc/>
        public int callbackOrder => 0;

        /// <inheritdoc/>
        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            if (LeanSettings.StripUIToolkit)
            {
#pragma warning disable CS0618
                Trim(data.inputDirectory);
#pragma warning restore CS0618
                Trim(InputsDirectory);
            }
            return WriteEmptyDocument();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Holding the module out of the player's compilation is what makes the strip honest: every use of
        /// UI Toolkit in player code becomes a compiler error naming its file and line, instead of a build
        /// that succeeds and carries the module anyway. A failing build is therefore the ordinary state of
        /// a project that has not been cleaned up yet, not an accident. The editor's own assemblies are
        /// already compiled when this runs, so the build cannot disturb them — but the exclusion belongs to
        /// the editor session rather than to the build, which is why <see cref="RestoreInclusion"/> is
        /// scheduled in the same breath.
        /// </remarks>
        public void OnPreprocessBuild(BuildReport report)
        {
            if (!LeanSettings.StripUIToolkit) return;
            if (!ModuleInclusion.Supported)
            {
                Debug.LogWarning("[Lean] This editor does not expose engine module inclusion, so only " +
                                 "the editor's own roots can be cut; player code that reaches UI Toolkit " +
                                 "keeps the module, and this build reports that when it ends.");
                return;
            }
            if (!ModuleInclusion.IsStrippable(EngineModule))
            {
                Debug.LogWarning($"[Lean] This editor reports '{EngineModule}' as not strippable, so " +
                                 "only the editor's own roots can be cut; player code that reaches UI " +
                                 "Toolkit keeps the module, and this build reports that when it ends.");
                return;
            }

            ModuleInclusion.Set(EngineModule, ModuleInclusionState.ForceExclude);
            EditorApplication.delayCall += RestoreInclusion;
            Debug.Log("[Lean] UI Toolkit is held out of this build's player compilation. " +
                      "Every compiler error below names a player file that uses it.");
        }

        /// <summary>Returns engine module inclusion to Unity's own decision.</summary>
        /// <remarks>
        /// Reached three ways, because leaving the module excluded is not harmless. A build that fails
        /// never reaches its postprocess callback, and the exclusion then survives in the editor session:
        /// the next script recompile compiles the **editor's** assemblies without the module too, and the
        /// project fills with errors that read as if the developer's own edits broke it. The delayed call
        /// scheduled alongside the exclusion is what actually covers a failed build — it runs on the first
        /// editor tick after the build returns, whichever way it went.
        /// </remarks>
        [MenuItem("Tools/LightSide/Lean/Restore module inclusion")]
        internal static void RestoreInclusion()
        {
            if (!ModuleInclusion.Supported) return;
            if (ModuleInclusion.Get(EngineModule) == ModuleInclusionState.Auto) return;
            ModuleInclusion.Set(EngineModule, ModuleInclusionState.Auto);
            Debug.Log($"[Lean] Engine module '{EngineModule}' is back under Unity's own decision.");
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The player's managed assemblies decide wherever the report lists them: holding the module out of
        /// the compilation removes the engine module by fiat, so the engine module list reads clean while
        /// the linker still carries UI Toolkit's managed code.
        /// </remarks>
        public void OnPostprocessBuild(BuildReport report)
        {
            RestoreInclusion();
            if (!LeanSettings.StripUIToolkit) return;

            var assemblies = report.GetFiles()
                .Select(file => Path.GetFileName(file.path))
                .Where(name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var stripping = report.strippingInfo;
            var modules = stripping?.includedModules.ToArray() ?? Array.Empty<string>();
            if (assemblies.Length == 0 && modules.Length == 0)
            {
                Debug.LogWarning("[Lean] This build reports neither its managed assemblies nor its engine " +
                                 "modules, so whether UI Toolkit left the player cannot be confirmed here.");
                return;
            }

            var managed = assemblies.Contains(Module + ".dll", StringComparer.OrdinalIgnoreCase);
            var included = modules
                .FirstOrDefault(module => module.StartsWith(EngineModule, StringComparison.OrdinalIgnoreCase));
            if (!managed && included == null)
            {
                Debug.Log("[Lean] UI Toolkit is not in this player.");
                return;
            }

            var kept = new[] { managed ? Module : null, included }.Where(name => name != null);
            var message = new StringBuilder()
                .AppendLine($"[Lean] UI Toolkit is still in this player: {string.Join(", ", kept)}.")
                .AppendLine("The linker still reaches it, so nothing was saved.");

            var holders = UIToolkitHolders.Scan();
            message.AppendLine();
            if (holders.Count > 0)
            {
                message.AppendLine("Player assemblies naming UI Toolkit types — guard or remove these uses:");
                var width = holders.Max(holder => holder.Assembly.Length);
                foreach (var holder in holders)
                    message.Append("    ").Append(holder.Assembly.PadRight(width))
                        .Append("  ").Append(holder.Types).AppendLine(holder.Types == 1 ? " type" : " types");
            }
            else if (UIToolkitHolders.Supported)
                message.AppendLine("No player script names a UI Toolkit type, so a link.xml, a precompiled")
                    .AppendLine("plugin or a linker root this editor wrote where Lean does not look keeps it.");
            else
                message.AppendLine("This editor does not expose the metadata reader needed to name the")
                    .AppendLine("assemblies responsible; read the build report's stripping section.");

            if (included != null) AppendNativeChain(message, stripping, included);
            Debug.LogWarning(message.ToString().TrimEnd());
        }

        /// <summary>
        /// The native side of the same answer: the module's own classes and, under each, what Unity
        /// recorded as requiring it. One level from a module names only its contents, which is why this
        /// walks down.
        /// </summary>
        private static void AppendNativeChain(StringBuilder message, StrippingInfo stripping, string module)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { module };
            var lines = new List<string>();
            foreach (var entity in stripping.GetReasonsForIncluding(module))
                Walk(stripping, entity, seen, lines, 0);
            if (lines.Count == 0) return;

            message.AppendLine().AppendLine($"Native classes Unity kept for '{module}':");
            foreach (var line in lines) message.Append("    ").AppendLine(line);
        }

        private static void Walk(StrippingInfo stripping, string entity, ISet<string> seen,
            ICollection<string> lines, int depth)
        {
            if (depth > 4 || !seen.Add(entity)) return;
            lines.Add(new string(' ', depth * 2) + entity);
            var reasons = stripping.GetReasonsForIncluding(entity).ToArray();
            foreach (var reason in reasons) Walk(stripping, reason, seen, lines, depth + 1);
        }

        private static void Trim(string directory)
        {
            foreach (var name in Inputs)
            {
                var path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;

                var document = XDocument.Load(path);
                var roots = document.Root?
                    .Elements("assembly")
                    .Where(element => (string)element.Attribute("fullname") == Module)
                    .ToArray();
                if (roots == null || roots.Length == 0) continue;

                var types = roots.SelectMany(root => root.Elements("type"))
                    .Select(type => (string)type.Attribute("fullname"))
                    .ToArray();
                foreach (var root in roots) root.Remove();
                document.Save(path);
                Debug.Log($"[Lean] Dropped from {name}: {string.Join(", ", types)}.");
            }
        }

        private static string WriteEmptyDocument()
        {
            Directory.CreateDirectory(LeanPaths.Work);
            var path = Path.Combine(LeanPaths.Work, OwnFile);
            new XDocument(new XElement("linker")).Save(path);
            return path;
        }
    }
}

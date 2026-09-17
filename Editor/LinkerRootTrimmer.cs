using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;
using UnityEngine;

namespace LightSide.LeanBuild
{
    /// <summary>
    /// Removes the UI Toolkit roots the editor seeds into the player's managed link, while
    /// <see cref="LeanBuildSettings.StripUIToolkit"/> is on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// UnityLinker takes its roots from the link XML that
    /// <c>UnityEditorInternal.AssemblyStripper.GetLinkXmlFiles</c> writes into
    /// <c>Library/Bee/artifacts/UnityLinkerInputs</c>, and the type lists behind those files come from
    /// the editor's own <c>RuntimeClassRegistry</c> — an editor always has UI Toolkit, so
    /// <c>PanelSettings</c> and <c>DynamicAtlasSettings</c> are rooted even in a player that cannot use
    /// them. The two roots keep <c>UnityEngine.UIElementsModule</c>, <c>UnityEngine.PropertiesModule</c>
    /// and <c>UnityEngine.InputForUIModule</c> whole and pull members out of a dozen more assemblies.
    /// </para>
    /// <para>
    /// The trim runs from <see cref="GenerateAdditionalLinkXmlFile"/> because that callback is the only
    /// public point between those files being written and UnityLinker reading them. An editor that
    /// reorders the two loses the saving and builds normally; it never produces a broken player.
    /// </para>
    /// </remarks>
    internal sealed class LinkerRootTrimmer : IUnityLinkerProcessor
    {
        private const string Module = "UnityEngine.UIElementsModule";
        private const string InputsDirectory = "Library/Bee/artifacts/UnityLinkerInputs";
        private const string OwnDirectory = "Library/LightSide";
        private const string OwnFile = "LeanBuild.link.xml";

        private static readonly string[] Inputs = { "TypesInScenes.xml", "SerializedTypes.xml" };

        /// <inheritdoc/>
        public int callbackOrder => 0;

        /// <inheritdoc/>
        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            if (LeanBuildSettings.StripUIToolkit) Trim();
            return WriteEmptyDocument();
        }

        private static void Trim()
        {
            foreach (var name in Inputs)
            {
                var path = Path.Combine(InputsDirectory, name);
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
                Debug.Log($"[LeanBuild] Dropped from {name}: {string.Join(", ", types)}.");
            }
        }

        private static string WriteEmptyDocument()
        {
            Directory.CreateDirectory(OwnDirectory);
            var path = Path.Combine(OwnDirectory, OwnFile);
            new XDocument(new XElement("linker")).Save(path);
            return path;
        }
    }
}

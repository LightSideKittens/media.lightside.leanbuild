using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace LightSide.Lean
{
    /// <summary>Names the player assemblies whose own code reaches UI Toolkit.</summary>
    /// <remarks>
    /// Reads the assemblies Unity compiles for the player before stripping, so the names are the ones a
    /// developer can act on rather than whatever survived the link. Bound by reflection to the Cecil the
    /// Editor ships with, because this package carries no dependencies; an Editor that no longer exposes
    /// it yields an empty list rather than an error.
    /// </remarks>
    internal static class UIToolkitHolders
    {
        private const string Directory = "Library/Bee/PlayerScriptAssemblies";
        private const string Module = "UnityEngine.UIElementsModule";

        private static readonly MethodInfo ReadAssembly;
        private static readonly PropertyInfo MainModule;
        private static readonly PropertyInfo AssemblyReferences;
        private static readonly MethodInfo GetTypeReferences;
        private static readonly PropertyInfo ScopeName;
        private static readonly PropertyInfo TypeScope;
        private static readonly PropertyInfo TypeFullName;

        static UIToolkitHolders()
        {
            Assembly cecil;
            try
            {
                cecil = Assembly.Load("Unity.Cecil");
            }
            catch (Exception)
            {
                return;
            }

            var assemblyDefinition = cecil.GetType("Mono.Cecil.AssemblyDefinition", false);
            var moduleDefinition = cecil.GetType("Mono.Cecil.ModuleDefinition", false);
            var scope = cecil.GetType("Mono.Cecil.IMetadataScope", false);
            var typeReference = cecil.GetType("Mono.Cecil.TypeReference", false);
            if (assemblyDefinition == null || moduleDefinition == null ||
                scope == null || typeReference == null) return;

            ReadAssembly = assemblyDefinition.GetMethod("ReadAssembly",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            MainModule = assemblyDefinition.GetProperty("MainModule");
            AssemblyReferences = moduleDefinition.GetProperty("AssemblyReferences");
            GetTypeReferences = moduleDefinition.GetMethod("GetTypeReferences", Type.EmptyTypes);
            ScopeName = scope.GetProperty("Name");
            TypeScope = typeReference.GetProperty("Scope");
            TypeFullName = typeReference.GetProperty("FullName");
        }

        /// <summary>Whether this Editor exposes the metadata reader the scan needs.</summary>
        internal static bool Supported =>
            ReadAssembly != null && MainModule != null && AssemblyReferences != null &&
            GetTypeReferences != null && ScopeName != null && TypeScope != null && TypeFullName != null;

        /// <summary>
        /// Every player assembly that names a UI Toolkit type, with how many distinct types it names,
        /// heaviest first. Empty when nothing does, when the assemblies are absent, or when unsupported.
        /// </summary>
        internal static IReadOnlyList<(string Assembly, int Types)> Scan()
        {
            var found = new List<(string, int)>();
            if (!Supported || !System.IO.Directory.Exists(Directory)) return found;

            foreach (var path in System.IO.Directory.GetFiles(Directory, "*.dll"))
            {
                IDisposable assembly = null;
                try
                {
                    assembly = (IDisposable)ReadAssembly.Invoke(null, new object[] { path });
                    var module = MainModule.GetValue(assembly);
                    if (!References(module)) continue;
                    found.Add((Path.GetFileNameWithoutExtension(path), CountTypes(module)));
                }
                catch (TargetInvocationException)
                {
                    continue;
                }
                finally
                {
                    assembly?.Dispose();
                }
            }

            found.Sort(static (a, b) => b.Item2.CompareTo(a.Item2));
            return found;
        }

        private static bool References(object module)
        {
            foreach (var reference in (IEnumerable)AssemblyReferences.GetValue(module))
                if ((string)ScopeName.GetValue(reference) == Module)
                    return true;
            return false;
        }

        private static int CountTypes(object module)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in (IEnumerable)GetTypeReferences.Invoke(module, null))
            {
                var scope = TypeScope.GetValue(type);
                if (scope == null || (string)ScopeName.GetValue(scope) != Module) continue;
                names.Add((string)TypeFullName.GetValue(type));
            }
            return names.Count;
        }
    }
}

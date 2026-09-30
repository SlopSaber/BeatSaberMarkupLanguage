using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;
using BeatSaberMarkupLanguage.Attributes;

namespace BeatSaberMarkupLanguage.Util
{
    // Prepared documents are transferred to exactly one parse, never shared with
    // macros or callers. Preserve XML line annotations and synchronous API behavior.
    internal static class MarkupPreparation
    {
        private const int Limit = 128;
        private const int CharacterLimit = 2 * 1024 * 1024;
        private static readonly object Gate = new();
        private static readonly Dictionary<(Assembly, string), string> Resources = new();
        private static readonly Dictionary<string, XDocument> Documents = new();
        private static readonly HashSet<(Assembly, string)> PendingResources = new();
        private static readonly HashSet<string> PendingDocuments = new();
        private static int resourceCharacters;
        private static int documentCharacters;
        private static int pendingCharacters;

        internal static bool TryGetResource(Assembly assembly, string resource, out string content)
        {
            lock (Gate)
            {
                return Resources.TryGetValue((assembly, resource), out content);
            }
        }

        internal static void PrewarmView(Type type)
        {
            // Only immutable type/assembly metadata is captured, never the controller.
            _ = PrepareView(type);
        }

        internal static void Prewarm(Assembly assembly, string resource, Type hostType)
        {
            if (assembly == null || string.IsNullOrEmpty(resource))
            {
                return;
            }

            lock (Gate)
            {
                if (Resources.ContainsKey((assembly, resource)) || PendingResources.Count >= Limit ||
                    !PendingResources.Add((assembly, resource)))
                {
                    return;
                }
            }

            _ = PrepareResource(assembly, resource, hostType);
        }

        internal static XDocument Take(string content)
        {
            if (content == null)
            {
                return XDocument.Parse(content, LoadOptions.SetLineInfo);
            }

            XDocument document = null;
            lock (Gate)
            {
                if (Documents.TryGetValue(content, out document))
                {
                    Documents.Remove(content);
                    documentCharacters -= content.Length;
                }
            }

            PrewarmContent(content);

            // Never wait for a worker from the main thread. Cold calls keep their
            // original synchronous parse and exception semantics.
            return document ?? XDocument.Parse(content, LoadOptions.SetLineInfo);
        }

        internal static void PrewarmContent(string content)
        {
            if (content == null || content.Length > CharacterLimit)
            {
                return;
            }

            lock (Gate)
            {
                if (Documents.ContainsKey(content) || PendingDocuments.Count >= Limit ||
                    pendingCharacters + content.Length > CharacterLimit || !PendingDocuments.Add(content))
                {
                    return;
                }

                pendingCharacters += content.Length;
            }

            _ = PrepareDocument(content);
        }

        private static async Task PrepareView(Type type)
        {
            try
            {
                string resource = await BackgroundWork.Run(() =>
                    type.GetCustomAttribute<ViewDefinitionAttribute>()?.Definition ??
                    ViewControllers.BSMLAutomaticViewController.GetDefaultResourceName(type)).ConfigureAwait(false);
                Prewarm(type.Assembly, resource, type);
            }
            catch
            {
                // The normal Content/Parse call still reports any failure.
            }
        }

        private static async Task PrepareResource(Assembly assembly, string resource, Type hostType)
        {
            try
            {
                (string Content, XDocument Document) prepared = await BackgroundWork.Run(() =>
                {
                    using Stream stream = assembly.GetManifestResourceStream(resource) ?? throw new ResourceNotFoundException(assembly, resource);
                    using StreamReader reader = new(stream);
                    string content = reader.ReadToEnd();
                    XDocument document = XDocument.Parse(content, LoadOptions.SetLineInfo);
                    if (hostType != null)
                    {
                        BSMLParser.PrewarmHost(hostType);
                    }

                    return (content, document);
                }).ConfigureAwait(false);
                lock (Gate)
                {
                    if (!Resources.ContainsKey((assembly, resource)) && Resources.Count < Limit &&
                        resourceCharacters + prepared.Content.Length <= CharacterLimit)
                    {
                        Resources.Add((assembly, resource), prepared.Content);
                        resourceCharacters += prepared.Content.Length;
                    }

                    StoreDocument(prepared.Content, prepared.Document);
                }
            }
            catch
            {
                // Speculation must not change when a synchronous caller sees errors.
            }
            finally
            {
                lock (Gate)
                {
                    PendingResources.Remove((assembly, resource));
                }
            }
        }

        private static async Task PrepareDocument(string content)
        {
            try
            {
                XDocument document = await BackgroundWork.Run(() => XDocument.Parse(content, LoadOptions.SetLineInfo)).ConfigureAwait(false);
                lock (Gate)
                {
                    StoreDocument(content, document);
                }
            }
            catch
            {
                // A real parse will throw with its normal XML line information.
            }
            finally
            {
                lock (Gate)
                {
                    PendingDocuments.Remove(content);
                    pendingCharacters -= content.Length;
                }
            }
        }

        private static void StoreDocument(string content, XDocument document)
        {
            if (!Documents.ContainsKey(content) && Documents.Count < Limit &&
                documentCharacters + content.Length <= CharacterLimit)
            {
                Documents.Add(content, document);
                documentCharacters += content.Length;
            }
        }
    }
}

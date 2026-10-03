using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Xml.Linq;
using BeatSaberMarkupLanguage.Attributes;

namespace BeatSaberMarkupLanguage.Util
{
    internal sealed class PreparedMarkup
    {
        [ThreadStatic]
        private static PreparedMarkup current;

        private XDocument document;
        private Exception contentError;
        private Exception parseError;
        private bool consumed;

        internal string Content { get; private set; }

        internal Exception FileError { get; private set; }

        internal static PreparedMarkup Read(Request request, CancellationToken cancellationToken)
        {
            PreparedMarkup prepared = new();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string resource = request.ResourceName;
                if (resource == null)
                {
                    ViewDefinitionAttribute definition = request.HostType.GetCustomAttribute<ViewDefinitionAttribute>();
                    resource = definition != null ? definition.Definition : ViewControllers.BSMLAutomaticViewController.GetDefaultResourceName(request.HostType);
                }

                if (File.Exists(request.FilePath))
                {
                    try
                    {
                        prepared.Content = File.ReadAllText(request.FilePath);
                    }
                    catch (Exception ex)
                    {
                        prepared.FileError = ex;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(prepared.Content) && !string.IsNullOrEmpty(resource))
                {
                    Assembly assembly = request.HostType.Assembly;
                    if (MarkupPreparation.TryGetResource(assembly, resource, out string cached))
                    {
                        prepared.Content = cached;
                    }
                    else
                    {
                        using Stream stream = assembly.GetManifestResourceStream(resource) ?? throw new ResourceNotFoundException(assembly, resource);
                        using StreamReader reader = new(stream);
                        prepared.Content = reader.ReadToEnd();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                prepared.contentError = ex;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (prepared.contentError == null)
            {
                try
                {
                    prepared.document = XDocument.Parse(prepared.Content, LoadOptions.SetLineInfo);
                }
                catch (Exception ex)
                {
                    prepared.parseError = ex;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return prepared;
        }

        internal static PreparedMarkup ReadResource(ResourceRequest request, CancellationToken cancellationToken)
        {
            PreparedMarkup prepared = new();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MarkupPreparation.TryGetResource(request.Assembly, request.ResourceName, out string cached))
                {
                    prepared.Content = cached;
                }
                else
                {
                    using Stream stream = request.Assembly.GetManifestResourceStream(request.ResourceName) ?? throw new ResourceNotFoundException(request.Assembly, request.ResourceName);
                    using StreamReader reader = new(stream);
                    prepared.Content = reader.ReadToEnd();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                prepared.contentError = ex;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (prepared.contentError == null)
            {
                try
                {
                    prepared.document = XDocument.Parse(prepared.Content, LoadOptions.SetLineInfo);
                }
                catch (Exception ex)
                {
                    prepared.parseError = ex;
                }
            }

            if (request.HostType != null)
            {
                try
                {
                    BSMLParser.PrewarmHost(request.HostType);
                }
                catch
                {
                    // Optional reflection preparation must not move binding failures.
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return prepared;
        }

        internal static bool TryTake(string content, out XDocument document)
        {
            PreparedMarkup prepared = current;
            document = null;
            if (prepared == null || prepared.contentError != null || prepared.consumed || !string.Equals(prepared.Content, content, StringComparison.Ordinal))
            {
                return false;
            }

            prepared.consumed = true;
            if (prepared.parseError != null)
            {
                ExceptionDispatchInfo.Capture(prepared.parseError).Throw();
            }

            document = prepared.document;
            prepared.document = null;
            return true;
        }

        internal string GetContent()
        {
            if (contentError != null)
            {
                ExceptionDispatchInfo.Capture(contentError).Throw();
            }

            return Content;
        }

        internal IDisposable Use() => new Scope(this);

        internal sealed class Request
        {
            internal Request(string filePath, Type hostType, string resourceName)
            {
                FilePath = filePath;
                HostType = hostType;
                ResourceName = resourceName;
            }

            internal string FilePath { get; }

            internal Type HostType { get; }

            internal string ResourceName { get; }
        }

        internal sealed class ResourceRequest
        {
            private ResourceRequest(Assembly assembly, string resourceName, Type hostType)
            {
                Assembly = assembly;
                ResourceName = resourceName;
                HostType = hostType;
            }

            internal Assembly Assembly { get; }

            internal string ResourceName { get; }

            internal Type HostType { get; }

            internal static ResourceRequest Capture(Assembly assembly, string resourceName, Type hostType)
            {
                // Assembly subclasses can implement arbitrary resource providers.
                return assembly?.GetType() == typeof(PreparedMarkup).Assembly.GetType() ? new(assembly, resourceName, hostType) : null;
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly PreparedMarkup previous;

            internal Scope(PreparedMarkup prepared)
            {
                previous = current;
                current = prepared;
            }

            public void Dispose()
            {
                current = previous;
            }
        }
    }
}

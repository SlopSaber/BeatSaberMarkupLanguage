#if DEBUG
#define HRVC_DEBUG
#endif
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Util;

namespace BeatSaberMarkupLanguage.ViewControllers
{
    public abstract class BSMLAutomaticViewController : BSMLViewController, WatcherGroup.IHotReloadableController
    {
        private string resourceName;
        private string content;
        private PreparedMarkup reloadContent;
        private bool refreshing;

        protected BSMLAutomaticViewController()
            : base()
        {
            HotReloadAttribute hotReloadAttr = GetType().GetCustomAttribute<HotReloadAttribute>();
            if (hotReloadAttr == null)
            {
                ContentFilePath = null;
                Util.MarkupPreparation.PrewarmView(GetType());
            }
            else
            {
                ContentFilePath = Path.ChangeExtension(hotReloadAttr.Path, ".bsml");
            }
        }

        public override string Content
        {
            get
            {
                if (reloadContent != null)
                {
                    return reloadContent.GetContent();
                }

                if (resourceName == null)
                {
                    ViewDefinitionAttribute viewDef = GetType().GetCustomAttribute<ViewDefinitionAttribute>();
                    if (viewDef != null)
                    {
                        resourceName = viewDef.Definition;
                    }
                    else
                    {
                        resourceName = GetDefaultResourceName(GetType());
                    }
                }

                if (string.IsNullOrEmpty(content))
                {
                    if (!string.IsNullOrEmpty(ContentFilePath) && File.Exists(ContentFilePath))
                    {
                        try
                        {
                            content = File.ReadAllText(ContentFilePath);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log?.Warn($"Unable to read file {ContentFilePath} for {name}: {ex.Message}");
                            Logger.Log?.Debug(ex);
                        }
                    }

                    if (string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(resourceName))
                    {
                        if (ContentFilePath != null)
                        {
#if HRVC_DEBUG
                            Logger.Log.Warn($"No content from file {ContentFilePath}, using resource {resourceName}");
#endif
                        }

                        content = Utilities.GetResourceContent(GetType().Assembly, resourceName);
                    }
                }

                return content;
            }
        }

        public bool ContentChanged { get; protected set; }

        public string ContentFilePath { get; }

        string WatcherGroup.IHotReloadableController.Name => name;

        bool WatcherGroup.IHotReloadableController.IsAlive => this;

        PreparedMarkup.Request WatcherGroup.IHotReloadableController.CaptureReload() => new(Path.GetFullPath(ContentFilePath), GetType(), resourceName);

        void WatcherGroup.IHotReloadableController.MarkDirty()
        {
            ContentChanged = true;
            content = null;
            reloadContent = null;
        }

        void WatcherGroup.IHotReloadableController.Refresh(bool forceReload, PreparedMarkup prepared)
        {
            if (!this || Plugin.IsQuitting || !isActivated || !isActiveAndEnabled)
            {
#if HRVC_DEBUG
                Logger.Log.Warn($"Trying to refresh {GetInstanceID()}:{name} when it isn't ActiveAndEnabled.");
#endif
                return;
            }

            if (ContentChanged || forceReload)
            {
                try
                {
                    refreshing = true;
                    __Deactivate(false, false, false);
                    ClearContents();
                    content = prepared?.Content;
                    reloadContent = prepared;
                    if (prepared?.FileError != null)
                    {
                        Logger.Log?.Warn($"Unable to read file {ContentFilePath} for {name}: {prepared.FileError.Message}");
                        Logger.Log?.Debug(prepared.FileError);
                    }

                    using (prepared?.Use())
                    {
                        __Activate(false, false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log?.Error(ex);
                }
                finally
                {
                    reloadContent = null;
                    refreshing = false;
                    if (!this || !isActivated || !isActiveAndEnabled)
                    {
                        WatcherGroup.UnregisterViewController(this);
                    }
                }
            }
        }

        internal static string GetDefaultResourceName(Type type)
        {
            string ns = type.Namespace;
            string name = type.Name;
            string resourceNoExtension = (ns.Length > 0 ? ns + "." : string.Empty) + name;

            // First we check with no extension in case DependentUpon is being used on the embedded resource
            return type.Assembly.GetManifestResourceNames().Contains(resourceNoExtension) ? resourceNoExtension : $"{resourceNoExtension}.bsml";
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            if (!string.IsNullOrEmpty(ContentFilePath))
            {
#if HRVC_DEBUG
                Logger.Log.Notice($"DidActivate: {GetInstanceID()}:{name}");
#endif

                if (ContentChanged && !firstActivation)
                {
                    ContentChanged = false;
                    ParseWithFallback();
                }
                else if (firstActivation)
                {
                    ParseWithFallback();
                }

                bool registered = refreshing || WatcherGroup.RegisterViewController(this);
#if HRVC_DEBUG
                if (registered)
                {
                    Logger.Log.Info($"Registered view controller {this.name} to watcher");
                }
                else
                {
                    Logger.Log.Error($"Failed to register view controller {this.name} to watcher");
                }
#endif
            }
            else if (firstActivation)
            {
                ParseWithFallback();
            }
        }

        protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            if (!string.IsNullOrEmpty(ContentFilePath))
            {
                content = null;
                reloadContent = null;
#if HRVC_DEBUG
                Logger.Log.Notice($"DidDeactivate: {GetInstanceID()}:{name}");
#endif
                if (!refreshing && !WatcherGroup.UnregisterViewController(this))
                {
#if HRVC_DEBUG
                    Logger.Log.Warn($"Failed to Unregister {GetInstanceID()}:{name}");
#endif
                }
            }

            base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
        }

        protected override void OnDestroy()
        {
            content = null;
            reloadContent = null;
            if (!string.IsNullOrEmpty(ContentFilePath))
            {
                WatcherGroup.UnregisterViewController(this);
            }

            base.OnDestroy();
        }
    }
}

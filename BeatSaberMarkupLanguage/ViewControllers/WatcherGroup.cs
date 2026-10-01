using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Util;
using IPA.Utilities.Async;

namespace BeatSaberMarkupLanguage.ViewControllers
{
    internal class WatcherGroup
    {
        private static readonly Dictionary<string, WatcherGroup> WatcherDictionary = new();

        private readonly TimeSpan hotReloadDelay = TimeSpan.FromSeconds(0.5f);
        private readonly Dictionary<int, Binding> boundControllers = new();
        private CancellationTokenSource reloadCancellation;

        internal WatcherGroup(string directory)
        {
            ContentDirectory = directory;
            CreateWatcher();
        }

        internal interface IHotReloadableController
        {
            bool ContentChanged { get; }

            string ContentFilePath { get; }

            string Name { get; }

            bool IsAlive { get; }

            void MarkDirty();

            PreparedMarkup.Request CaptureReload();

            void Refresh(bool forceReload = false, PreparedMarkup prepared = null);

            int GetInstanceID();
        }

        internal FileSystemWatcher Watcher { get; private set; }

        internal string ContentDirectory { get; }

        internal bool IsReloading { get; private set; }

        public static bool RegisterViewController(IHotReloadableController controller)
        {
            string contentFile = controller.ContentFilePath;
            if (Plugin.IsQuitting || !controller.IsAlive || string.IsNullOrEmpty(contentFile))
            {
                return false;
            }

            string contentDirectory = Path.GetDirectoryName(contentFile);
            if (!Directory.Exists(contentDirectory))
            {
                return false;
            }

            if (!WatcherDictionary.TryGetValue(contentDirectory, out WatcherGroup watcherGroup))
            {
                watcherGroup = new WatcherGroup(contentDirectory);
                WatcherDictionary.Add(contentDirectory, watcherGroup);
            }

            watcherGroup.BindController(controller);
            return true;
        }

        public static bool UnregisterViewController(IHotReloadableController controller)
        {
            string contentFile = controller.ContentFilePath;
            if (string.IsNullOrEmpty(contentFile))
            {
                return false;
            }

            string contentDirectory = Path.GetDirectoryName(contentFile);
            return WatcherDictionary.TryGetValue(contentDirectory, out WatcherGroup watcherGroup) && watcherGroup.UnbindController(controller);
        }

        internal static void Shutdown()
        {
            foreach (WatcherGroup group in WatcherDictionary.Values)
            {
                group.DestroyWatcher();
                group.boundControllers.Clear();
            }

            WatcherDictionary.Clear();
        }

        internal bool BindController(IHotReloadableController controller)
        {
            int instanceId = controller.GetInstanceID();
            if (boundControllers.ContainsKey(instanceId))
            {
                return false;
            }

            boundControllers.Add(instanceId, new Binding(controller));
            CreateWatcher();
            Watcher.EnableRaisingEvents = true;
            return true;
        }

        internal bool UnbindController(int instanceId)
        {
            bool removed = boundControllers.Remove(instanceId);
            if (boundControllers.Count == 0)
            {
                DestroyWatcher();
            }

            return removed;
        }

        internal bool UnbindController(IHotReloadableController controller) => controller != null && UnbindController(controller.GetInstanceID());

        private static void Observe(Task task)
        {
            _ = task.ContinueWith(failed => Logger.Log?.Error($"Failed to dispatch hot reload\n{failed.Exception}"), TaskContinuationOptions.OnlyOnFaulted);
        }

        private void CreateWatcher()
        {
            if (Watcher != null || Plugin.IsQuitting || !Directory.Exists(ContentDirectory))
            {
                return;
            }

            Watcher = new FileSystemWatcher(ContentDirectory, "*.bsml")
            {
                NotifyFilter = NotifyFilters.LastWrite,
            };
            Watcher.Changed += OnFileWasChanged;
        }

        private void DestroyWatcher()
        {
            FileSystemWatcher watcher = Watcher;
            Watcher = null;
            reloadCancellation?.Cancel();
            if (watcher != null)
            {
                watcher.Changed -= OnFileWasChanged;
                watcher.Dispose();
            }
        }

        private void OnFileWasChanged(object sender, FileSystemEventArgs e)
        {
            string fullPath = e.FullPath;
            // Watcher callbacks only dispatch immutable event data. All bindings,
            // controller checks and queue state belong to the main thread.
            Observe(UnityMainThreadTaskScheduler.Factory.StartNew(() => HandleChange(sender, fullPath)));
        }

        private void HandleChange(object sender, string fullPath)
        {
            if (Plugin.IsQuitting || !ReferenceEquals(sender, Watcher))
            {
                return;
            }

            foreach (KeyValuePair<int, Binding> pair in boundControllers.ToArray())
            {
                Binding binding = pair.Value;
                if (!binding.Controller.TryGetTarget(out IHotReloadableController controller) || !controller.IsAlive)
                {
                    UnbindController(pair.Key);
                    continue;
                }

                if (string.Equals(fullPath, binding.FullPath, StringComparison.Ordinal))
                {
                    controller.MarkDirty();
                    binding.Revision++;
                    binding.Pending = true;
                }
            }

            StartReloadIfNeeded();
        }

        private void StartReloadIfNeeded()
        {
            if (Plugin.IsQuitting || Watcher == null || IsReloading || !boundControllers.Values.Any(binding => binding.Pending))
            {
                return;
            }

            IsReloading = true;
            reloadCancellation = new CancellationTokenSource();
            Observe(ReloadAsync(Watcher, reloadCancellation));
        }

        private List<ReloadRequest> CaptureRequests(FileSystemWatcher watcher)
        {
            List<ReloadRequest> requests = new();
            if (Plugin.IsQuitting || !ReferenceEquals(watcher, Watcher))
            {
                return requests;
            }

            foreach (KeyValuePair<int, Binding> pair in boundControllers.ToArray())
            {
                Binding binding = pair.Value;
                if (!binding.Controller.TryGetTarget(out IHotReloadableController controller) || !controller.IsAlive)
                {
                    UnbindController(pair.Key);
                    continue;
                }

                if (binding.Pending)
                {
                    binding.Pending = false;
                    requests.Add(new ReloadRequest(pair.Key, binding, binding.Revision, controller.CaptureReload()));
                }
            }

            return requests;
        }

        private void Publish(FileSystemWatcher watcher, ReloadRequest request, PreparedMarkup prepared)
        {
            if (Plugin.IsQuitting || !ReferenceEquals(watcher, Watcher) ||
                !boundControllers.TryGetValue(request.InstanceId, out Binding binding) || !ReferenceEquals(binding, request.Binding) ||
                binding.Revision != request.Revision || !binding.Controller.TryGetTarget(out IHotReloadableController controller) || !controller.IsAlive)
            {
                return;
            }

            controller.Refresh(prepared: prepared);
        }

        private async Task ReloadAsync(FileSystemWatcher watcher, CancellationTokenSource cancellation)
        {
            CancellationToken token = cancellation.Token;
            try
            {
                bool pending;
                do
                {
                    await Task.Delay(hotReloadDelay, token).ConfigureAwait(false);
                    List<ReloadRequest> requests = await UnityMainThreadTaskScheduler.Factory.StartNew(() => CaptureRequests(watcher)).ConfigureAwait(false);
                    foreach (ReloadRequest request in requests)
                    {
                        PreparedMarkup.Request input = request.Input;
                        PreparedMarkup prepared = await BackgroundWork.Run(() => PreparedMarkup.Read(input, token), token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        await UnityMainThreadTaskScheduler.Factory.StartNew(() => Publish(watcher, request, prepared)).ConfigureAwait(false);
                    }

                    pending = await UnityMainThreadTaskScheduler.Factory.StartNew(() =>
                        !Plugin.IsQuitting && ReferenceEquals(watcher, Watcher) && boundControllers.Values.Any(binding => binding.Pending)).ConfigureAwait(false);
                }
                while (pending);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Logger.Log?.Error($"Failed to reload markup\n{ex}");
            }
            finally
            {
                await UnityMainThreadTaskScheduler.Factory.StartNew(() =>
                {
                    if (ReferenceEquals(reloadCancellation, cancellation))
                    {
                        reloadCancellation = null;
                        IsReloading = false;
                        // Changes arriving between the final check and cleanup
                        // remain pending and start a new owned request.
                        StartReloadIfNeeded();
                    }
                }).ConfigureAwait(false);
                cancellation.Dispose();
            }
        }

        private sealed class Binding
        {
            internal Binding(IHotReloadableController controller)
            {
                Controller = new WeakReference<IHotReloadableController>(controller);
                FullPath = Path.GetFullPath(controller.ContentFilePath);
            }

            internal WeakReference<IHotReloadableController> Controller { get; }

            internal string FullPath { get; }

            internal long Revision { get; set; }

            internal bool Pending { get; set; }
        }

        private sealed class ReloadRequest
        {
            internal ReloadRequest(int instanceId, Binding binding, long revision, PreparedMarkup.Request input)
            {
                InstanceId = instanceId;
                Binding = binding;
                Revision = revision;
                Input = input;
            }

            internal int InstanceId { get; }

            internal Binding Binding { get; }

            internal long Revision { get; }

            internal PreparedMarkup.Request Input { get; }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Util;
using HMUI;
using UnityEngine;
using Zenject;
using Object = UnityEngine.Object;

namespace BeatSaberMarkupLanguage.GameplaySetup
{
    public class GameplaySetup : ZenjectSingleton<GameplaySetup>, TableView.IDataSource, IInitializable, IDisposable
    {
        private const string ReuseIdentifier = "GameplaySetupCell";
        private const string ViewResource = "BeatSaberMarkupLanguage.Views.gameplay-setup.bsml";
        private const string CellResource = "BeatSaberMarkupLanguage.Views.gameplay-setup-cell.bsml";

        private readonly MainFlowCoordinator mainFlowCoordinator;
        private readonly GameplaySetupViewController gameplaySetupViewController;
        private readonly HierarchyManager hierarchyManager;
        private readonly CancellationTokenSource lifetimeCancellation = new();
        private readonly SortedList<GameplaySetupMenu> menus = new(Comparer<GameplaySetupMenu>.Create((a, b) => a.Name.CompareTo(b.Name)));

        private Task debounceTask;
        private Task<PreparedMarkup[]> preparationTask;
        private long revision;
        private bool initialized;
        private bool disposed;
        private bool resetToVanilla;
        private GameplaySetupMenu[] parsingMenus;
        private MenuDataSource menuDataSource;

        [UIObject("root-object")]
        private GameObject rootObject;

        [UIComponent("new-tab-selector")]
        private TabSelector tabSelector;

        [UIComponent("vanilla-tab")]
        private Transform vanillaTab;

        [UIComponent("mods-tab")]
        private Transform modsTab;

        [UIComponent("list-modal")]
        private ModalView listModal;

        [UIComponent("mods-list")]
        private CustomListTableData modsList;

        [UIValue("vanilla-items")]
        private List<Transform> vanillaItems = [];

        private GameplaySetup(MainFlowCoordinator mainFlowCoordinator, GameplaySetupViewController gameplaySetupViewController, HierarchyManager hierarchyManager)
        {
            this.mainFlowCoordinator = mainFlowCoordinator;
            this.gameplaySetupViewController = gameplaySetupViewController;
            this.hierarchyManager = hierarchyManager;
        }

        [UIValue("has-menus")]
        private bool HasMenus => (parsingMenus?.Length ?? menus.Count) > 0;

        [UIValue("mod-menus")]
        private IEnumerable<GameplaySetupMenu> ParsingMenus => parsingMenus ?? (IEnumerable<GameplaySetupMenu>)menus;

        [UIValue("anchored-position")]
        private float AnchoredPosition => HasMenus ? -4 : 0;

        [UIValue("size-delta")]
        private float SizeDelta => HasMenus ? -8 : 0;

        public void AddTab(string name, string resource, object host)
        {
            AddTab(Assembly.GetCallingAssembly(), name, resource, host, MenuType.All);
        }

        public void AddTab(string name, string resource, object host, MenuType menuType)
        {
            AddTab(Assembly.GetCallingAssembly(), name, resource, host, menuType);
        }

        /// <summary>
        /// Remove a tab.
        /// </summary>
        /// <param name="name">The name of the tab.</param>
        public void RemoveTab(string name)
        {
            int count = menus.Count;
            menus.RemoveAll(m => m.Name == name);
            if (menus.Count != count)
            {
                revision++;
            }

            if (initialized)
            {
                QueueRefreshView();
            }
        }

        /// <inheritdoc />
        public float CellSize(int idx) => 8f;

        /// <inheritdoc />
        public int NumberOfCells() => menus.Count;

        /// <inheritdoc />
        public TableCell CellForIdx(TableView tableView, int idx)
        {
            return CreateCell(idx, null, null);
        }

        /// <inheritdoc />
        public void Initialize()
        {
            initialized = true;
            foreach (Transform transform in gameplaySetupViewController.transform)
            {
                if (transform.name != "HeaderPanel")
                {
                    vanillaItems.Add(transform);
                }
            }

            QueueRefreshView();

            gameplaySetupViewController.didActivateEvent += GameplaySetupDidActivate;
            gameplaySetupViewController.didDeactivateEvent += GameplaySetupDidDeactivate;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            revision++;
            lifetimeCancellation.Cancel();
            gameplaySetupViewController.didActivateEvent -= GameplaySetupDidActivate;
            gameplaySetupViewController.didDeactivateEvent -= GameplaySetupDidDeactivate;
            RetireView();

            if (debounceTask == null)
            {
                lifetimeCancellation.Dispose();
            }
        }

        private static Task<PreparedMarkup[]> PrepareMarkupAsync(PreparedMarkup.ResourceRequest[] requests, CancellationToken cancellationToken)
        {
            return BackgroundWork.Run(
                () =>
                {
                    PreparedMarkup[] prepared = new PreparedMarkup[requests.Length];
                    for (int i = 0; i < requests.Length; i++)
                    {
                        prepared[i] = PreparedMarkup.ReadResource(requests[i], cancellationToken);
                    }

                    return prepared;
                },
                cancellationToken);
        }

        private TableCell CreateCell(int idx, GameplaySetupMenu menu, MenuDataSource source)
        {
            long cellRevision = revision;
            source?.CheckCurrent(cellRevision);
            GameplaySetupCell tableCell = (GameplaySetupCell)modsList.TableView.DequeueReusableCellForIdentifier(ReuseIdentifier);
            bool created = tableCell == null;
            GameObject cellObject = null;
            try
            {
                if (created)
                {
                    cellObject = new GameObject(nameof(GameplaySetupCell));
                    tableCell = cellObject.AddComponent<GameplaySetupCell>();
                    source?.CheckCurrent(cellRevision);
                    tableCell.interactable = true;
                    source?.CheckCurrent(cellRevision);
                    tableCell.reuseIdentifier = ReuseIdentifier;

                    PreparedMarkup prepared = source?.TakeCell();
                    using IDisposable scope = prepared?.Use();
                    BSMLParser.Instance.Parse(
                        prepared != null ? prepared.GetContent() : Utilities.GetResourceContent(Assembly.GetExecutingAssembly(), CellResource),
                        tableCell.gameObject,
                        tableCell);
                }

                source?.CheckCurrent(cellRevision);
                tableCell.PopulateCell(source != null ? menu : menus[idx]);
                source?.CheckCurrent(cellRevision);
                return tableCell;
            }
            catch
            {
                if (cellObject != null)
                {
                    Object.Destroy(cellObject);
                }

                throw;
            }
        }

        private void QueueRefreshView()
        {
            if (!disposed && !Plugin.IsQuitting)
            {
                debounceTask ??= RefreshViewCoroutine();
            }
        }

        private async Task RefreshViewCoroutine()
        {
            Dictionary<GameplaySetupMenu, (PreparedMarkup Content, PreparedMarkup Error)> ready = new();
            Queue<PreparedMarkup> cells = new();
            PreparedMarkup root = null;
            long refreshRevision = revision;
            try
            {
                await Task.Yield();
                while (IsOwnerAvailable())
                {
                    long currentRevision = revision;
                    refreshRevision = currentRevision;
                    GameplaySetupMenu[] cohort = menus.ToArray();
                    foreach (GameplaySetupMenu removed in ready.Keys.Where(menu => !cohort.Contains(menu)).ToArray())
                    {
                        ready.Remove(removed);
                    }

                    while (cells.Count > cohort.Length)
                    {
                        cells.Dequeue();
                    }

                    List<PreparedMarkup.ResourceRequest> requests = new();
                    int rootIndex = -1;
                    if (root == null)
                    {
                        rootIndex = requests.Count;
                        requests.Add(PreparedMarkup.ResourceRequest.Capture(Assembly.GetExecutingAssembly(), ViewResource, GetType()));
                    }

                    List<(GameplaySetupMenu Menu, int ContentIndex, int ErrorIndex)> bindings = new();
                    foreach (GameplaySetupMenu menu in cohort)
                    {
                        if (ready.ContainsKey(menu))
                        {
                            continue;
                        }

                        PreparedMarkup.ResourceRequest content = PreparedMarkup.ResourceRequest.Capture(menu.Assembly, menu.Resource, menu.Host?.GetType());
                        int contentIndex = -1;
                        if (content != null)
                        {
                            contentIndex = requests.Count;
                            requests.Add(content);
                        }

                        int errorIndex = requests.Count;
                        requests.Add(PreparedMarkup.ResourceRequest.Capture(Assembly.GetExecutingAssembly(), GameplaySetupMenu.ErrorViewResourcePath, null));
                        bindings.Add((menu, contentIndex, errorIndex));
                    }

                    int firstCellIndex = requests.Count;
                    for (int i = cells.Count; i < cohort.Length; i++)
                    {
                        requests.Add(PreparedMarkup.ResourceRequest.Capture(Assembly.GetExecutingAssembly(), CellResource, typeof(GameplaySetupCell)));
                    }

                    if (requests.Count > 0)
                    {
                        preparationTask = PrepareMarkupAsync(requests.ToArray(), lifetimeCancellation.Token);
                        PreparedMarkup[] prepared = await preparationTask;
                        preparationTask = null;
                        if (!IsOwnerAvailable())
                        {
                            return;
                        }

                        if (rootIndex >= 0)
                        {
                            root = prepared[rootIndex];
                        }

                        foreach ((GameplaySetupMenu menu, int contentIndex, int errorIndex) in bindings)
                        {
                            ready[menu] = (contentIndex >= 0 ? prepared[contentIndex] : null, prepared[errorIndex]);
                        }

                        for (int i = firstCellIndex; i < prepared.Length; i++)
                        {
                            cells.Enqueue(prepared[i]);
                        }
                    }

                    if (revision != currentRevision)
                    {
                        continue;
                    }

                    foreach (GameplaySetupMenu menu in cohort)
                    {
                        (PreparedMarkup content, PreparedMarkup error) = ready[menu];
                        menu.Prepare(content, error, () => IsCurrent(currentRevision));
                    }

                    bool published;
                    try
                    {
                        published = RefreshView(root, cohort, cells, currentRevision);
                    }
                    finally
                    {
                        foreach (GameplaySetupMenu menu in cohort)
                        {
                            menu.ClearPreparation();
                        }

                        root = null;
                        ready.Clear();
                    }

                    if (published)
                    {
                        return;
                    }

                    await Task.Yield();
                }
            }
            catch (OperationCanceledException) when (disposed || Plugin.IsQuitting)
            {
            }
            catch (Exception ex)
            {
                if (IsOwnerAvailable())
                {
                    Logger.Log.Error($"Error refreshing gameplay settings\n{ex}");
                }
            }
            finally
            {
                preparationTask = null;
                debounceTask = null;
                if (disposed)
                {
                    lifetimeCancellation.Dispose();
                }
                else if (IsOwnerAvailable() && revision != refreshRevision)
                {
                    QueueRefreshView();
                }
            }
        }

        private bool RefreshView(PreparedMarkup prepared, GameplaySetupMenu[] cohort, Queue<PreparedMarkup> cells, long currentRevision)
        {
            if (!IsCurrent(currentRevision))
            {
                return false;
            }

            RetireView();
            if (!IsCurrent(currentRevision))
            {
                return false;
            }

            parsingMenus = cohort;
            try
            {
                using IDisposable scope = prepared.Use();
                BSMLParser.Instance.Parse(prepared.GetContent(), gameplaySetupViewController.gameObject, this);
                if (!IsCurrent(currentRevision) || rootObject == null || modsList == null || listModal == null)
                {
                    RetireView();
                    return false;
                }

                UpdateTabsVisibility();
                if (!IsCurrent(currentRevision) || rootObject == null || modsList == null || modsList.TableView == null || listModal == null)
                {
                    RetireView();
                    return false;
                }

                menuDataSource = new(this, cohort, cells, rootObject, modsList.TableView);
                modsList.TableView.SetDataSource(menuDataSource, true);
                if (!IsCurrent(currentRevision) || rootObject == null || listModal == null)
                {
                    RetireView();
                    return false;
                }

                if (resetToVanilla)
                {
                    ResetToVanilla();
                }

                if (!IsCurrent(currentRevision) || rootObject == null || listModal == null)
                {
                    RetireView();
                    return false;
                }

                listModal.blockerClickedEvent += ClickedOffModal;
                return true;
            }
            catch when (!IsCurrent(currentRevision))
            {
                RetireView();
                return false;
            }
            catch
            {
                RetireView();
                throw;
            }
            finally
            {
                parsingMenus = null;
            }
        }

        private bool IsOwnerAvailable()
        {
            return !disposed && !Plugin.IsQuitting && mainFlowCoordinator != null && IsNativeOwnerAvailable();
        }

        private bool IsNativeOwnerAvailable()
        {
            return gameplaySetupViewController != null && hierarchyManager != null && hierarchyManager._screenSystem != null && hierarchyManager._screenSystem.mainScreen != null &&
                !hierarchyManager._screenSystem.mainScreen.isBeingDestroyed;
        }

        private bool IsCurrent(long currentRevision) => IsOwnerAvailable() && revision == currentRevision;

        private void RetireView()
        {
            GameObject previousRoot = rootObject;
            ModalView previousModal = listModal;
            MenuDataSource previousSource = menuDataSource;
            menuDataSource = null;
            rootObject = null;
            tabSelector = null;
            vanillaTab = null;
            modsTab = null;
            listModal = null;
            modsList = null;
            previousSource?.RetireMenus();
            foreach (GameplaySetupMenu menu in parsingMenus ?? menus.ToArray())
            {
                menu.RetireView();
            }

            if (previousModal != null)
            {
                previousModal.blockerClickedEvent -= ClickedOffModal;
            }

            if (IsNativeOwnerAvailable())
            {
                Transform destination = gameplaySetupViewController.transform;
                foreach (Transform item in vanillaItems)
                {
                    if (!IsNativeOwnerAvailable() || destination == null)
                    {
                        break;
                    }

                    if (item != null && item.parent != destination)
                    {
                        item.SetParent(destination, false);
                    }
                }
            }

            if (previousRoot != null)
            {
                previousRoot.SetActive(false);
                Object.Destroy(previousRoot);
            }
        }

        private void AddTab(Assembly assembly, string name, string resource, object host, MenuType menuType)
        {
            if (menus.Any(m => m.Name == name))
            {
                return;
            }

            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (string.IsNullOrEmpty(resource))
            {
                throw new ArgumentNullException(nameof(resource));
            }

            // Delegate dispatch or wrappers can identify the caller as a helper assembly.
            // Use the host's assembly only when it actually owns the requested resource.
            if (assembly.GetManifestResourceInfo(resource) == null && host != null)
            {
                Assembly hostAssembly = host.GetType().Assembly;
                if (hostAssembly.GetManifestResourceInfo(resource) != null)
                {
                    assembly = hostAssembly;
                }
            }

            GameplaySetupMenu menu = new(name, resource, host, assembly, menuType);
            menus.Add(menu);
            revision++;

            if (initialized)
            {
                QueueRefreshView();
            }
        }

        private void GameplaySetupDidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            UpdateTabsVisibility();
        }

        private void GameplaySetupDidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            if (removedFromHierarchy || screenSystemDisabling || !IsOwnerAvailable())
            {
                return;
            }

            resetToVanilla = true;
            ResetToVanilla();
        }

        private void ResetToVanilla()
        {
            if (!IsOwnerAvailable() || tabSelector == null || vanillaTab == null || modsTab == null)
            {
                return;
            }

            long currentRevision = revision;
            tabSelector.TextSegmentedControl.SelectCellWithNumber(0);
            if (!IsCurrent(currentRevision) || vanillaTab == null || modsTab == null)
            {
                return;
            }

            vanillaTab.gameObject.SetActive(true);
            if (!IsCurrent(currentRevision) || modsTab == null)
            {
                return;
            }

            modsTab.gameObject.SetActive(false);
            resetToVanilla = false;
        }

        private void ClickedOffModal()
        {
            UpdateTabsVisibility();
        }

        private void UpdateTabsVisibility()
        {
            if (!IsOwnerAvailable())
            {
                return;
            }

            long currentRevision = revision;
            MenuType menuType = mainFlowCoordinator.YoungestChildFlowCoordinatorOrSelf() switch
            {
                CampaignFlowCoordinator => MenuType.Campaign,
                SinglePlayerLevelSelectionFlowCoordinator => MenuType.Solo,
                GameServerLobbyFlowCoordinator => MenuType.Online,
                _ => MenuType.Custom,
            };

            foreach (GameplaySetupMenu menu in ParsingMenus.ToArray())
            {
                menu.SetVisible(menu.IsMenuType(menuType));
                if (!IsCurrent(currentRevision))
                {
                    return;
                }
            }
        }

        [UIAction("show-modal")]
        private void ShowModal()
        {
            if (IsOwnerAvailable() && listModal != null)
            {
                listModal.Show(true, true);
            }
        }

        private sealed class MenuDataSource : TableView.IDataSource
        {
            private readonly GameplaySetup owner;
            private readonly GameplaySetupMenu[] cohort;
            private readonly Queue<PreparedMarkup> cells;
            private readonly GameObject root;
            private readonly TableView table;

            internal MenuDataSource(GameplaySetup owner, GameplaySetupMenu[] cohort, Queue<PreparedMarkup> cells, GameObject root, TableView table)
            {
                this.owner = owner;
                this.cohort = cohort;
                this.cells = cells;
                this.root = root;
                this.table = table;
            }

            public float CellSize(int idx) => owner.CellSize(idx);

            public int NumberOfCells() => cohort.Length;

            public TableCell CellForIdx(TableView tableView, int idx)
            {
                if (tableView != table)
                {
                    throw new OperationCanceledException();
                }

                return owner.CreateCell(idx, cohort[idx], this);
            }

            internal PreparedMarkup TakeCell() => cells.Count > 0 ? cells.Dequeue() : null;

            internal void RetireMenus()
            {
                cells.Clear();
                foreach (GameplaySetupMenu menu in cohort)
                {
                    menu.RetireView();
                }
            }

            internal void CheckCurrent(long currentRevision)
            {
                if (!owner.IsCurrent(currentRevision) || owner.menuDataSource != this || root == null || root != owner.rootObject ||
                    table == null || owner.modsList == null || table != owner.modsList.TableView)
                {
                    throw new OperationCanceledException();
                }
            }
        }
    }
}

using System;
using System.Reflection;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Util;
using UnityEngine;

namespace BeatSaberMarkupLanguage.GameplaySetup
{
    internal class GameplaySetupMenu
    {
        internal const string ErrorViewResourcePath = "BeatSaberMarkupLanguage.Views.gameplay-tab-error.bsml";

        private PreparedMarkup preparedContent;
        private PreparedMarkup preparedError;
        private Func<bool> isCurrent;

        [UIObject("root-tab")]
        private GameObject tabObject;

        [UIComponent("root-tab")]
        private Tab tab;

        public GameplaySetupMenu(string name, string resource, object host, Assembly assembly, MenuType menuType)
        {
            this.Name = name;
            this.Resource = resource;
            this.Host = host;
            this.Assembly = assembly;
            this.MenuType = menuType;
        }

        public string Resource { get; }

        public object Host { get; }

        public Assembly Assembly { get; }

        public MenuType MenuType { get; }

        [UIValue("tab-name")]
        public string Name { get; }

        public bool Visible
        {
            get => !Plugin.Config.HiddenTabs.Contains(Name);
            set
            {
                if (value)
                {
                    Plugin.Config.HiddenTabs.Remove(Name);
                }
                else if (Visible)
                {
                    Plugin.Config.HiddenTabs.Add(Name);
                }
            }
        }

        [UIAction("#post-parse")]
        public void Setup()
        {
            CheckCurrent();
            try
            {
                using IDisposable scope = preparedContent?.Use();
                string content = preparedContent != null ? preparedContent.GetContent() : Utilities.GetResourceContent(Assembly, Resource);
                BSMLParser.Instance.Parse(content, tabObject, Host);
                CheckCurrent();
            }
            catch (Exception ex)
            {
                CheckCurrent();
                Logger.Log.Error($"Error adding gameplay settings tab for {Assembly?.GetName().Name ?? "<NULL>"} ({Name})\n{ex}");
                CheckCurrent();
                using IDisposable scope = preparedError?.Use();
                string content = preparedError != null ? preparedError.GetContent() : Utilities.GetResourceContent(Assembly.GetExecutingAssembly(), ErrorViewResourcePath);
                BSMLParser.Instance.Parse(content, tabObject);
                CheckCurrent();
            }
        }

        internal void Prepare(PreparedMarkup content, PreparedMarkup error, Func<bool> current)
        {
            preparedContent = content;
            preparedError = error;
            isCurrent = current;
        }

        internal void ClearPreparation()
        {
            preparedContent = null;
            preparedError = null;
            isCurrent = null;
        }

        internal void RetireView()
        {
            tabObject = null;
            tab = null;
        }

        public void SetVisible(bool isVisible)
        {
            if (tab != null)
            {
                tab.IsVisible = Visible && isVisible;
            }
        }

        public bool IsMenuType(MenuType toCheck)
        {
            return (MenuType & toCheck) == toCheck;
        }

        private void CheckCurrent()
        {
            if (isCurrent != null && (!isCurrent() || tabObject == null))
            {
                throw new OperationCanceledException();
            }
        }
    }
}

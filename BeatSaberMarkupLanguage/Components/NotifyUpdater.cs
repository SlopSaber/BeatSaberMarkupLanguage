using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Threading.Tasks;
using IPA.Utilities;
using IPA.Utilities.Async;
using UnityEngine;

namespace BeatSaberMarkupLanguage.Components
{
    public class NotifyUpdater : MonoBehaviour
    {
        private readonly Dictionary<string, PropertyAction> actionDict = new();
        private INotifyPropertyChanged notifyHost;
        private PropertyChangedEventHandler notifyHandler;
        private int notifyRevision;

        internal INotifyPropertyChanged NotifyHost
        {
            get => notifyHost;
            set
            {
                if (notifyHost != null)
                {
                    this.notifyHost.PropertyChanged -= notifyHandler;
                }

                notifyHost = value;
                notifyHandler = null;
                int revision = ++notifyRevision;

                if (notifyHost != null)
                {
                    INotifyPropertyChanged host = notifyHost;
                    notifyHandler = (sender, args) => NotifyHost_PropertyChanged(host, revision, sender, args.PropertyName);
                    host.PropertyChanged += notifyHandler;
                }
            }
        }

        internal bool AddAction(string propertyName, Action<object> action)
        {
            if (notifyHost == null)
            {
                return false;
            }

            if (actionDict.TryGetValue(propertyName, out PropertyAction notify))
            {
                notify.AddAction(action);
            }
            else
            {
                PropertyInfo prop = notifyHost.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (prop == null)
                {
                    Logger.Log.Error($"No property '{propertyName}' on object of type '{notifyHost.GetType().FullName}'");
                    return false;
                }

                if (prop.GetMethod == null)
                {
                    Logger.Log.Error($"Property '{propertyName}' on object of type '{notifyHost.GetType().FullName}' does not have a getter");
                    return false;
                }

                actionDict.Add(propertyName, new PropertyAction(prop, action));
            }

            return true;
        }

        protected void OnDestroy()
        {
            NotifyHost = null;
            actionDict.Clear();
        }

        private void NotifyHost_PropertyChanged(INotifyPropertyChanged host, int revision, object sender, string propertyName)
        {
            if (UnityGame.OnMainThread)
            {
                ApplyPropertyChanged(host, revision, sender, propertyName);
                return;
            }

            UnityMainThreadTaskScheduler.Factory.StartNew(() => ApplyPropertyChanged(host, revision, sender, propertyName))
                .ContinueWith(task => Logger.Log.Error($"Failed to apply property notification\n{task.Exception}"), TaskContinuationOptions.OnlyOnFaulted);
        }

        private void ApplyPropertyChanged(INotifyPropertyChanged host, int revision, object sender, string propertyName)
        {
            Utilities.EnsureRunningOnMainThread();
            if (Plugin.IsQuitting || this == null || revision != notifyRevision || !ReferenceEquals(host, notifyHost))
            {
                return;
            }

            // https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.propertychangedeventargs.propertyname?view=netframework-4.7.2#remarks
            if (string.IsNullOrEmpty(propertyName))
            {
                foreach (PropertyAction propertyAction in actionDict.Values)
                {
                    propertyAction.Invoke(sender);
                }
            }
            else if (actionDict.TryGetValue(propertyName, out PropertyAction propertyAction))
            {
                propertyAction.Invoke(sender);
            }
        }

        private class PropertyAction
        {
            private readonly PropertyInfo property;

            private Action<object> action;

            internal PropertyAction(PropertyInfo property, Action<object> action)
            {
                this.property = property;
                this.action = action;
            }

            internal void AddAction(Action<object> newAction)
            {
                action += newAction;
            }

            internal void Invoke(object sender)
            {
                action.Invoke(property.GetValue(sender));
            }
        }
    }
}

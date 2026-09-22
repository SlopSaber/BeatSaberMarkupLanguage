using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using HMUI;
using IPA.Utilities.Async;

namespace BeatSaberMarkupLanguage.Harmony_Patches
{
    // ModalView.Hide reparents the modal and changes its active state. Neither
    // operation is safe inside a parent's disable/destroy notification.
    [HarmonyPatch]
    internal static class DeferBSMLModalCleanup
    {
        private static readonly HashSet<ModalView> Pending = new();

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ModalView), nameof(ModalView.OnDisable));
            yield return AccessTools.Method(typeof(ModalView), nameof(ModalView.HandleParentViewControllerDidDeactivate));
        }

        [HarmonyPrefix]
        private static bool Prefix(ModalView __instance)
        {
            if (__instance.name != "BSMLModal" || !__instance._isShown)
            {
                return true;
            }

            if (!Plugin.IsQuitting && Pending.Add(__instance))
            {
                UnityMainThreadTaskScheduler.Factory.StartNew(() =>
                {
                    Pending.Remove(__instance);
                    if (__instance == null || Plugin.IsQuitting || !__instance._isShown)
                    {
                        return;
                    }

                    bool animateParent = __instance._animateParentCanvas;
                    __instance._animateParentCanvas = false;
                    try
                    {
                        __instance.Hide(false);
                    }
                    finally
                    {
                        if (__instance != null)
                        {
                            __instance._animateParentCanvas = animateParent;
                        }
                    }
                }).ContinueWith(task => Logger.Log.Error(task.Exception), TaskContinuationOptions.OnlyOnFaulted);
            }

            // OnDestroy still owns blocker destruction during application exit.
            return false;
        }
    }
}

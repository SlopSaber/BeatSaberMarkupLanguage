using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using IPA.Utilities;
using IPA.Utilities.Async;
using UnityEngine;
using UnityEngine.UI;

namespace BeatSaberMarkupLanguage.TypeHandlers
{
    [ComponentHandler(typeof(RawImage))]
    internal class RawImageHandler : TypeHandler<RawImage>
    {
        private static readonly ConditionalWeakTable<RawImage, ImageRequestState> Requests = new();

        public override Dictionary<string, string[]> Props => new()
        {
            { "image", new[] { "source", "src" } },
        };

        public override Dictionary<string, Action<RawImage, string>> Setters => new()
        {
            { "image", new Action<RawImage, string>(SetImage) },
        };

        public void SetImage(RawImage image, string imagePath)
        {
            ImageRequestState state = Requests.GetValue(image, _ => new ImageRequestState());
            int revision = state.Advance();
            bool cachedImage = imagePath.Length > 1 && imagePath[0] == '#';
            if (cachedImage && UnityGame.OnMainThread)
            {
                SetCachedImage(image, imagePath, state, revision);
            }
            else
            {
                UnityMainThreadTaskScheduler.Factory.StartNew(async () =>
                {
                    if (!IsCurrentRequest(image, state, revision))
                    {
                        return;
                    }

                    if (cachedImage)
                    {
                        SetCachedImage(image, imagePath, state, revision);
                        return;
                    }

                    byte[] data = await Utilities.GetDataAsync(imagePath);
                    if (!IsCurrentRequest(image, state, revision))
                    {
                        return;
                    }

                    Texture2D texture = await Utilities.LoadImageAsync(data);
                    if (!IsCurrentRequest(image, state, revision))
                    {
                        UnityEngine.Object.Destroy(texture);
                        return;
                    }

                    image.texture = texture;
                }).Unwrap().ContinueWith((task) => Logger.Log.Error($"Failed to load image '{imagePath}'\n{task.Exception}"), TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        private static bool IsCurrentRequest(RawImage image, ImageRequestState state, int revision)
        {
            Utilities.EnsureRunningOnMainThread();
            return !Plugin.IsQuitting && image != null && state.IsCurrent(revision);
        }

        private static void SetCachedImage(RawImage image, string imagePath, ImageRequestState state, int revision)
        {
            if (!IsCurrentRequest(image, state, revision))
            {
                return;
            }

            string imgName = imagePath.Substring(1);
            image.texture = Utilities.FindTextureCached(imgName);
            if (image.texture == null)
            {
                Logger.Log.Error($"Could not find {nameof(Texture)} with image name '{imgName}'");
            }
        }

        private sealed class ImageRequestState
        {
            private int revision;

            internal int Advance() => Interlocked.Increment(ref revision);

            internal bool IsCurrent(int value) => Volatile.Read(ref revision) == value;
        }
    }
}

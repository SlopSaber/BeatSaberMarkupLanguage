using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Util;
using IPA.Utilities.Async;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BeatSaberMarkupLanguage.Animations
{
    public class AnimationLoader
    {
        public static async Task<AnimationData> ProcessApngAsync(byte[] data)
        {
            AnimationInfo animationInfo = await APNGUnityDecoder.ProcessAsync(data);
            return await ProcessAnimationInfoAsync(animationInfo);
        }

        public static async Task<AnimationData> ProcessGifAsync(byte[] data)
        {
            AnimationInfo animationInfo = await GIFUnityDecoder.ProcessAsync(data);
            return await ProcessAnimationInfoAsync(animationInfo);
        }

        private static async Task<AnimationData> ProcessAnimationInfoAsync(AnimationInfo animationInfo)
        {
            int limit = await UnityMainThreadTaskScheduler.Factory.StartNew(() =>
            {
                Utilities.EnsureRunningOnMainThread();
                if (Plugin.IsQuitting)
                {
                    throw new System.OperationCanceledException();
                }

                return System.Math.Min(SystemInfo.maxTextureSize, 4096);
            }).ConfigureAwait(false);
            AnimationAtlas prepared = await BackgroundWork.Run(() => AnimationAtlas.Prepare(animationInfo, limit)).ConfigureAwait(false);
            animationInfo = null;

            return await UnityMainThreadTaskScheduler.Factory.StartNew(() => CreateAtlas(prepared)).ConfigureAwait(false);
        }

        private static AnimationData CreateAtlas(AnimationAtlas prepared)
        {
            Utilities.EnsureRunningOnMainThread();
            if (Plugin.IsQuitting)
            {
                throw new System.OperationCanceledException();
            }

            // The decoded frames are uncompressed and have no mipmaps, so the
            // original packed atlas uses RGBA32 without mipmaps as well.
            Texture2D texture = new(prepared.Width, prepared.Height, TextureFormat.RGBA32, false)
            {
                name = "AnimatedImageAtlas",
            };
            try
            {
                texture.LoadRawTextureData(prepared.Colors);
                texture.Apply(false, true);
                return new AnimationData(texture, prepared.Uvs, prepared.Delays, prepared.SourceWidth, prepared.SourceHeight);
            }
            catch
            {
                Object.Destroy(texture);
                throw;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace BeatSaberMarkupLanguage.Animations
{
    public class APNGUnityDecoder
    {
        private const double ByteInverse = 1.0 / 255;

        public static Task<AnimationInfo> ProcessAsync(byte[] apngData)
        {
            return Util.BackgroundWork.Run(() => ProcessingThread(apngData));
        }

        private static AnimationInfo ProcessingThread(byte[] apngData)
        {
            using MemoryStream stream = new(apngData, false);
            APNG.APNG apng = APNG.APNG.FromStream(stream);
            int frameCount = apng.FrameCount;
            Frame[] sourceFrames = apng.Frames;

            List<FrameInfo> frames = new(frameCount);

            FrameInfo prevFrame = default;

            for (int i = 0; i < frameCount; i++)
            {
                Frame apngFrame = sourceFrames[i];

                using (Bitmap bitmap = apngFrame.ToBitmap())
                {
                    FrameInfo frameInfo = new(bitmap.Width, bitmap.Height);

                    bitmap.MakeTransparent(Color.Black);
                    bitmap.RotateFlip(RotateFlipType.Rotate180FlipX);

                    BitmapData frame = bitmap.LockBits(new Rectangle(Point.Empty, apng.ActualSize), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

                    try
                    {
                        Marshal.Copy(frame.Scan0, frameInfo.Colors, 0, frameInfo.Colors.Length);
                    }
                    finally
                    {
                        bitmap.UnlockBits(frame);
                    }

                    if (apngFrame.FcTLChunk.BlendOp == APNG.Chunks.BlendOps.APNGBlendOpOver && i > 0)
                    {
                        // BGRA
                        byte[] last = prevFrame.Colors;
                        byte[] src = frameInfo.Colors;

                        for (int alpha = frameInfo.Colors.Length - 1; alpha > 2; alpha -= 4)
                        {
                            double srcA = src[alpha] * ByteInverse;
                            double lastA = last[alpha] * ByteInverse;
                            double blendedA = srcA + ((1 - srcA) * lastA);
                            for (int c = 1; c <= 3; c++)
                            {
                                int channel = alpha - c;
                                src[channel] = blendedA == 0 ? (byte)0 :
                                    (byte)Math.Round(((srcA * src[channel]) + ((1 - srcA) * lastA * last[channel])) / blendedA);
                            }

                            src[alpha] = (byte)Math.Round(blendedA * 255);
                        }
                    }

                    frameInfo.Delay = apngFrame.FrameRate;
                    frames.Add(frameInfo);
                    prevFrame = frameInfo;
                }
            }

            return new AnimationInfo(frames);
        }
    }
}

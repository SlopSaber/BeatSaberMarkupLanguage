using System;
using UnityEngine;

namespace BeatSaberMarkupLanguage.Animations
{
    internal record AnimationAtlas(int Width, int Height, byte[] Colors, Rect[] Uvs, float[] Delays, int SourceWidth, int SourceHeight)
    {
        private const int Padding = 2;

        internal static AnimationAtlas Prepare(AnimationInfo animation, int limit)
        {
            FrameInfo first = animation.Frames[0];
            int count = animation.Frames.Count;
            foreach (FrameInfo frame in animation.Frames)
            {
                // Both decoders produce full-canvas BGRA frames owned by this load.
                if (frame.Width <= 0 || frame.Height <= 0 || frame.Width != first.Width || frame.Height != first.Height ||
                    frame.Colors == null || frame.Colors.Length != checked(frame.Width * frame.Height * 4))
                {
                    throw new ArgumentException("Animation frames must contain complete, equal-sized BGRA canvases.", nameof(animation));
                }
            }

            Layout layout = FindLayout(first.Width, first.Height, count, limit, 1);
            if (layout == null)
            {
                layout = FindLayout(first.Width, first.Height, count, limit, 0) ??
                    throw new ArgumentException("Animation frames cannot fit in the maximum atlas size.", nameof(animation));
                double lower = 0;
                double upper = 1;
                for (int i = 0; i < 32; i++)
                {
                    double scale = (lower + upper) / 2;
                    Layout candidate = FindLayout(first.Width, first.Height, count, limit, scale);
                    if (candidate == null)
                    {
                        upper = scale;
                    }
                    else
                    {
                        lower = scale;
                        layout = candidate;
                    }
                }
            }

            byte[] colors = new byte[checked(layout.Width * layout.Height * 4)];
            Rect[] uvs = new Rect[count];
            float[] delays = new float[count];
            for (int i = 0; i < count; i++)
            {
                int x = (i % layout.Columns) * (layout.FrameWidth + Padding);
                int y = (i / layout.Columns) * (layout.FrameHeight + Padding);
                CopyFrame(animation.Frames[i], layout, x, y, colors);
                uvs[i] = new Rect((float)x / layout.Width, (float)y / layout.Height, (float)layout.FrameWidth / layout.Width, (float)layout.FrameHeight / layout.Height);
                delays[i] = animation.Frames[i].Delay;
            }

            return new AnimationAtlas(layout.Width, layout.Height, colors, uvs, delays, first.Width, first.Height);
        }

        private static Layout FindLayout(int sourceWidth, int sourceHeight, int count, int limit, double scale)
        {
            int frameWidth = Math.Max(1, (int)Math.Floor(sourceWidth * scale));
            int frameHeight = Math.Max(1, (int)Math.Floor(sourceHeight * scale));
            if (frameWidth > limit || frameHeight > limit)
            {
                return null;
            }

            Layout best = null;
            int maxColumns = Math.Min(count, (limit + Padding) / (frameWidth + Padding));
            for (int columns = 1; columns <= maxColumns; columns++)
            {
                int rows = ((count - 1) / columns) + 1;
                long usedHeight = ((long)rows * (frameHeight + Padding)) - Padding;
                if (usedHeight > limit)
                {
                    continue;
                }

                int width = RoundSize((columns * (frameWidth + Padding)) - Padding, limit);
                int height = RoundSize((int)usedHeight, limit);
                long area = (long)width * height;
                if (best == null || area < (long)best.Width * best.Height ||
                    (area == (long)best.Width * best.Height && Math.Max(width, height) < Math.Max(best.Width, best.Height)))
                {
                    best = new Layout(width, height, frameWidth, frameHeight, columns);
                }
            }

            return best;
        }

        private static int RoundSize(int value, int limit)
        {
            int size = 1;
            while (size < value && size < limit)
            {
                size *= 2;
            }

            return Math.Min(size, limit);
        }

        private static void CopyFrame(FrameInfo frame, Layout layout, int offsetX, int offsetY, byte[] atlas)
        {
            bool resize = frame.Width != layout.FrameWidth || frame.Height != layout.FrameHeight;
            for (int y = 0; y < layout.FrameHeight; y++)
            {
                int target = (((offsetY + y) * layout.Width) + offsetX) * 4;
                if (!resize)
                {
                    int source = y * frame.Width * 4;
                    for (int x = 0; x < frame.Width; x++)
                    {
                        atlas[target++] = frame.Colors[source + 2];
                        atlas[target++] = frame.Colors[source + 1];
                        atlas[target++] = frame.Colors[source];
                        atlas[target++] = frame.Colors[source + 3];
                        source += 4;
                    }

                    continue;
                }

                double sourceY = Math.Max(0, ((y + 0.5) * frame.Height / layout.FrameHeight) - 0.5);
                int y0 = (int)sourceY;
                int y1 = Math.Min(y0 + 1, frame.Height - 1);
                double weightY = sourceY - y0;
                for (int x = 0; x < layout.FrameWidth; x++)
                {
                    double sourceX = Math.Max(0, ((x + 0.5) * frame.Width / layout.FrameWidth) - 0.5);
                    int x0 = (int)sourceX;
                    int x1 = Math.Min(x0 + 1, frame.Width - 1);
                    double weightX = sourceX - x0;
                    int p00 = ((y0 * frame.Width) + x0) * 4;
                    int p10 = ((y0 * frame.Width) + x1) * 4;
                    int p01 = ((y1 * frame.Width) + x0) * 4;
                    int p11 = ((y1 * frame.Width) + x1) * 4;
                    for (int channel = 0; channel < 4; channel++)
                    {
                        int sourceChannel = channel < 3 ? 2 - channel : 3;
                        double bottom = (frame.Colors[p00 + sourceChannel] * (1 - weightX)) + (frame.Colors[p10 + sourceChannel] * weightX);
                        double top = (frame.Colors[p01 + sourceChannel] * (1 - weightX)) + (frame.Colors[p11 + sourceChannel] * weightX);
                        atlas[target++] = (byte)Math.Round((bottom * (1 - weightY)) + (top * weightY));
                    }
                }
            }
        }

        private sealed record Layout(int Width, int Height, int FrameWidth, int FrameHeight, int Columns);
    }
}

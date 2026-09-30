using System;
using System.Threading;
using System.Threading.Tasks;

namespace BeatSaberMarkupLanguage.Util
{
    // Pure managed work only. Callers await normally to resume their Unity context.
    internal static class BackgroundWork
    {
        private static readonly SemaphoreSlim Slots = new(2, 2);

        internal static async Task<T> Run<T>(Func<T> work)
        {
            await Slots.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(work).ConfigureAwait(false);
            }
            finally
            {
                Slots.Release();
            }
        }
    }
}

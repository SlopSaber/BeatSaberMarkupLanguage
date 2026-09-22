using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using HMUI;

namespace BeatSaberMarkupLanguage.Harmony_Patches
{
    /// <summary>
    /// Reduces the minimum handle height for compact BSML scroll views.
    /// </summary>
    [HarmonyPatch(typeof(VerticalScrollIndicator), nameof(VerticalScrollIndicator.RefreshHandle))]
    internal class VerticalScrollIndicator_RefreshHandle
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return new CodeMatcher(instructions)

                // make the minimum size delta of the scroll indicator handle 2 instead of 10
                // The game now calculates travel using the actual handle height.
                .MatchForward(false, new CodeMatch(OpCodes.Ldc_R4, 10f))
                .SetOperandAndAdvance(2f)
                .InstructionEnumeration();
        }
    }
}

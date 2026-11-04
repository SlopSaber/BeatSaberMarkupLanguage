using UnityEngine;
using UnityEngine.EventSystems;

namespace BeatSaberMarkupLanguage.Components.Settings
{
    /// <summary>
    /// Keeps drag events that start on a slider's increment or decrement button from bubbling to the slider,
    /// which would otherwise move the value to the pointer position beyond the sliding area.
    /// </summary>
    internal class SliderButtonDragBlocker : MonoBehaviour, IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
        }

        public void OnDrag(PointerEventData eventData)
        {
        }

        public void OnEndDrag(PointerEventData eventData)
        {
        }
    }
}

using UnityEngine;
using UnityEngine.UI;

namespace BikeGame
{
    /// <summary>Карточка байка в магазине: картинка, название, описание и доска ВЫБРАТЬ / ВЫБРАНО. Создаёт сборщик меню.</summary>
    public class BikeCard : MonoBehaviour
    {
        public int index;
        [Tooltip("Доска карточки: обычная / выбранная (золотая рамка).")]
        public Image card;
        [Tooltip("Кнопка: ВЫБРАТЬ / ВЫБРАНО.")]
        public Image button;
        [HideInInspector] public MainMenu menu;

        private void Awake()
        {
            Button b = button != null ? button.GetComponent<Button>() : null;
            if (b != null) b.onClick.AddListener(() => { if (menu != null) menu.PickBike(index); });
            Button cb = card != null ? card.GetComponent<Button>() : null;
            if (cb != null) cb.onClick.AddListener(() => { if (menu != null) menu.PickBike(index); });
        }

        public void SetSelected(bool selected, Sprite cardSprite, Sprite cardSelSprite, Sprite selectSprite, Sprite selectedSprite)
        {
            if (card != null && cardSprite != null) card.sprite = selected ? cardSelSprite : cardSprite;
            if (button != null && selectSprite != null) button.sprite = selected ? selectedSprite : selectSprite;
        }
    }
}

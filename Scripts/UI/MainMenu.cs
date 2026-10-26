using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace BikeGame
{
    /// <summary>
    /// Главное меню: ИГРАТЬ — открывает ленту локаций (картинки на весь экран, листается вверх-вниз; нажатие — заезд по ней),
    /// МАГАЗИН (выбор байка — карточки с картинкой, названием и кнопкой ВЫБРАТЬ; выбранный байк сразу проезжает на фоне и едет
    /// в игре), ВЫЙТИ. «Назад» на телефоне закрывает выбор локации / магазин или выходит из игры.
    /// </summary>
    public class MainMenu : MonoBehaviour
    {
        public string gameScene = "FreeRide";
        public Button playButton, shopButton, exitButton, closeShopButton;
        public GameObject shopPanel;
        [Tooltip("Окно выбора локации (открывает ИГРАТЬ).")]
        public GameObject locationPanel;
        [Tooltip("Лента локаций (листается вверх-вниз).")]
        public LocationFeed locationFeed;
        public Button closeLocationsButton;
        public Text bestText;
        [Tooltip("Что прятать, пока открыт магазин (вывеска с названием): магазин — на том же фоне с ботом на трейле, без меню.")]
        public GameObject[] hideInShop;
        [Tooltip("Гонщик на фоне — на нём сразу видно выбранные цвета.")]
        public BikeVisual previewRider;
        public Sprite swatchSprite, swatchSelectedSprite;
        [Header("Магазин байков")]
        public Sprite cardSprite, cardSelectedSprite, selectSprite, selectedSprite;

        private readonly List<ColorSwatch> _swatches = new List<ColorSwatch>();
        private readonly List<BikeCard> _cards = new List<BikeCard>();
        private readonly List<LocationCard> _locations = new List<LocationCard>();
        private static bool _reopenShop;      // после выбора байка меню перезагружается — магазин остаётся открытым

        private void Start()
        {
            Application.targetFrameRate = 60;      // на телефоне по умолчанию 30 кадров/с — меню дёргалось
            if (playButton != null) playButton.onClick.AddListener(Play);
            if (shopButton != null) shopButton.onClick.AddListener(() => SetShop(true));
            if (closeShopButton != null) closeShopButton.onClick.AddListener(() => SetShop(false));
            if (closeLocationsButton != null) closeLocationsButton.onClick.AddListener(() => SetLocations(false));
            if (exitButton != null) exitButton.onClick.AddListener(Quit);
            if (bestText != null)
            {
                float best = FreeRideManager.BestDistance;
                bestText.text = best > 0f ? $"Record: {best:0} m" : "";
            }
            if (shopPanel != null)
            {
                shopPanel.GetComponentsInChildren(true, _swatches);
                shopPanel.GetComponentsInChildren(true, _cards);
            }
            foreach (ColorSwatch s in _swatches) s.menu = this;
            foreach (BikeCard c in _cards) c.menu = this;
            if (locationPanel != null) locationPanel.GetComponentsInChildren(true, _locations);
            foreach (LocationCard c in _locations) c.menu = this;
            if (locationPanel != null) locationPanel.SetActive(false);
            SetShop(_reopenShop);
            _reopenShop = false;
            RefreshSwatches();
            RefreshCards();
        }

        private void Update()
        {
            if (!BackPressed()) return;
            if (locationPanel != null && locationPanel.activeSelf) SetLocations(false);
            else if (shopPanel != null && shopPanel.activeSelf) SetShop(false);
            else Quit();
        }

        /// <summary>ИГРАТЬ: открыть выбор локации (если окна нет — сразу в игру).</summary>
        public void Play()
        {
            if (locationPanel == null) { SceneManager.LoadScene(gameScene); return; }
            SetLocations(true);
        }

        /// <summary>Окно выбора локации: прячет кнопки меню и вывеску, подсвечивает последнюю выбранную локацию.</summary>
        public void SetLocations(bool open)
        {
            if (locationPanel == null) return;
            locationPanel.SetActive(open);
            SetMainButtons(!open);
            string cur = Locations.Selected;
            int at = 0;
            for (int i = 0; i < _locations.Count; i++)
            {
                _locations[i].SetSelected(_locations[i].id == cur);
                if (_locations[i].id == cur) at = i;
            }
            if (open && locationFeed != null) locationFeed.JumpTo(at);       // лента открывается на последней выбранной
        }

        /// <summary>Выбрана локация: запомнить и начать заезд по ней.</summary>
        public void PickLocation(string id)
        {
            Locations.Selected = id;
            SceneManager.LoadScene(gameScene);
        }

        private void SetMainButtons(bool show)
        {
            if (playButton != null) playButton.gameObject.SetActive(show);
            if (shopButton != null) shopButton.gameObject.SetActive(show);
            if (exitButton != null) exitButton.gameObject.SetActive(show);
            if (bestText != null) bestText.gameObject.SetActive(show);
            if (hideInShop != null) foreach (GameObject g in hideInShop) if (g != null) g.SetActive(show);
        }

        public void SetShop(bool open)
        {
            if (shopPanel != null) shopPanel.SetActive(open);
            SetMainButtons(!open);
        }

        public void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        /// <summary>Выбран байк в магазине: сохранить; меню перезагружается, и на фоне проезжает уже новый байк.</summary>
        public void PickBike(int index)
        {
            if (index == PlayerBike.Index) return;
            PlayerBike.Set(index);
            RefreshCards();
            _reopenShop = true;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex >= 0 ? SceneManager.GetActiveScene().name : gameScene);
        }

        private void RefreshCards()
        {
            foreach (BikeCard c in _cards)
                c.SetSelected(c.index == PlayerBike.Index, cardSprite, cardSelectedSprite, selectSprite, selectedSprite);
        }

        /// <summary>Выбран цвет в магазине: сохранить и сразу показать на гонщике.</summary>
        public void Pick(string key, Color c)
        {
            PlayerLook.Set(key, c);
            RefreshSwatches();
            ApplyPreview();
        }

        private void ApplyPreview()
        {
            if (previewRider == null || previewRider.skin == null) return;
            BikeSkin sk = previewRider.skin;
            previewRider.ApplyColors(PlayerLook.Get(PlayerLook.JerseyKey, sk.jerseyColor), PlayerLook.Get(PlayerLook.PantsKey, sk.shortsColor),
                                     PlayerLook.Get(PlayerLook.HelmetKey, sk.helmetColor));
        }

        private void RefreshSwatches()
        {
            BikeSkin sk = previewRider != null ? previewRider.skin : null;
            foreach (ColorSwatch s in _swatches)
            {
                Color def = sk == null ? Color.white : (s.key == PlayerLook.JerseyKey ? sk.jerseyColor : s.key == PlayerLook.PantsKey ? sk.shortsColor : sk.helmetColor);
                Color cur = PlayerLook.Get(s.key, def);
                bool sel = Mathf.Abs(cur.r - s.color.r) + Mathf.Abs(cur.g - s.color.g) + Mathf.Abs(cur.b - s.color.b) < 0.03f;
                if (s.frame != null) s.frame.sprite = sel ? swatchSelectedSprite : swatchSprite;
            }
        }

        private static bool BackPressed()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            return kb != null && kb.escapeKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Escape);
#endif
        }
    }
}

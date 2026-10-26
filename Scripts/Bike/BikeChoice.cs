using UnityEngine;

namespace BikeGame
{
    /// <summary>
    /// Байки, которые можно выбрать в магазине: у каждого свои настройки физики (BikeConfig) и внешний вид (BikeSkin).
    /// До того, как байк соберёт физику и спрайты, подставляет выбранный (PlayerBike). Висит на объекте байка; заполняет
    /// сборщик сцены. Порядок в списках = порядок карточек в магазине.
    /// </summary>
    [DefaultExecutionOrder(-1000)]   // раньше BikeController2D / RiderBody / BikeVisual
    public class BikeChoice : MonoBehaviour
    {
        [System.Serializable]
        public class Model
        {
            public string displayName = "Bike";
            [TextArea] public string description = "";
            public BikeConfig config;
            public BikeSkin skin;
            [Tooltip("Картинка для карточки в магазине.")]
            public Sprite preview;
        }

        public Model[] models = new Model[0];

        /// <summary>Выбранная модель (или первая, если номер вне списка).</summary>
        public Model Current => models == null || models.Length == 0 ? null : models[Mathf.Clamp(PlayerBike.Index, 0, models.Length - 1)];

        private void Awake()
        {
            Model m = Current;
            if (m == null) return;
            var bike = GetComponent<BikeController2D>();
            var visual = GetComponent<BikeVisual>();
            if (bike != null && m.config != null) bike.config = m.config;
            if (visual != null && m.skin != null) visual.skin = m.skin;
        }
    }
}

using UnityEngine;

namespace BikeGame
{
    /// <summary>Какой байк выбран в магазине (номер в списке BikeChoice) — хранится в PlayerPrefs, действует в меню и в игре.</summary>
    public static class PlayerBike
    {
        public const string Key = "bike_model";
        public static int Index => Mathf.Max(0, PlayerPrefs.GetInt(Key, 0));

        public static void Set(int index)
        {
            PlayerPrefs.SetInt(Key, Mathf.Max(0, index));
            PlayerPrefs.Save();
        }
    }
}

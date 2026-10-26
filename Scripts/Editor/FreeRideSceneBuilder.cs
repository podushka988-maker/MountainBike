using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BikeGame.EditorTools
{
    /// <summary>
    /// Меню Bike → «Создать сцену Free Ride».
    /// Одной командой собирает всю сцену: байк, трассу, фон, камеру, интерфейс, кнопки для телефона,
    /// создаёт нужные ассеты (материал, настройки байка) и добавляет сцену в Build Settings.
    /// Ничего настраивать руками не нужно. Повторный запуск пересоздаёт сцену заново.
    /// </summary>
    public static partial class FreeRideSceneBuilder
    {
        private const string Root = "Assets/_Project";
        public const string ScenePath = Root + "/Scenes/FreeRide.unity";
        private const string MaterialPath = Root + "/Materials/VertexColorUnlit.mat";
        private const string ConfigPath = Root + "/Settings/BikeConfig_Default.asset";
        private const string GroundPhysicsPath = Root + "/Settings/Ground.physicsMaterial2D";
        private const string SkinPath = Root + "/Settings/BikeSkin_Default.asset";
        private const string HardtailConfigPath = Root + "/Settings/BikeConfig_Hardtail.asset";
        private const string HardtailSkinPath = Root + "/Settings/BikeSkin_Hardtail.asset";
        /// <summary>Байки магазина (BikeChoice на байке): двухподвес по умолчанию и хардтейл.</summary>
        private static BikeChoice.Model[] _models;
        public const string ArtFolder = Root + "/Art/Bike";
        private const string EnvArt = Root + "/Art";
        public const float SpritePPU = 256f;

        private const float WheelRadius = 0.35f;
        private const float WheelBase = 1.1f;

        [MenuItem("Bike/Создать сцену Free Ride")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            PrepareShared(out Material mat, out BikeConfig config, out PhysicsMaterial2D groundPhys, out BikeSkin skin);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            GameObject camGo = CreateCameraAndBackground(mat);

            TerrainGenerator terrain = CreateTerrain(mat, groundPhys);

            // ---------- байк ----------
            BikeController2D bike = BuildBike(mat, config, skin, new Vector2(2f, 0f));
            terrain.target = bike.frame.transform;
            terrain.wheels = new[] { bike.rearWheel.transform, bike.frontWheel.transform };
            var follow = camGo.AddComponent<CameraFollow2D>();
            follow.bike = bike;
            follow.terrain = terrain;

            // ---------- атмосфера: пыль из-под колёс, встряска при жёстком приземлении, пылинки в вечернем свете ----------
            var fx = bike.gameObject.AddComponent<BikeEffects>();
            fx.bike = bike;
            fx.cameraFollow = follow;
            fx.dustMaterial = GetOrCreateTexMaterial("Dust", EnvArt + "/FX/dust.png");
            var motes = camGo.AddComponent<AmbientMotes>();
            motes.moteMaterial = GetOrCreateTexMaterial("Mote", EnvArt + "/FX/mote.png");
            WireLocations(camGo, terrain, motes, mat, BackgroundThemes.Pick.Chosen);       // локация — выбранная в меню

            // ---------- звук: ветер, шины, трещотка втулки, юз, приземления, падение; фон — лес ----------
            var audio = bike.gameObject.AddComponent<BikeAudio>();
            audio.bike = bike;
            audio.wind = Clip("wind_loop"); audio.roll = Clip("roll_dirt_loop"); audio.freehub = Clip("freehub_loop"); audio.freehubSlow = Clip("freehub_slow_loop"); audio.pedal = Clip("pedal_loop");
            audio.skid = Clip("skid_loop"); audio.crash = Clip("crash"); audio.frameCrack = Clip("frame_crack");
            audio.landings = new[] { Clip("land_1"), Clip("land_2"), Clip("land_3") };
            // стайки птиц вдалеке над лесом
            var birds = camGo.AddComponent<BirdFlock>();
            birds.material = mat;
            birds.frames = new[] { 0, 1, 2 }.Select(f => AssetDatabase.LoadAssetAtPath<Sprite>($"{EnvArt}/FX/bird_{f}.png")).ToArray();
            var ambient = camGo.AddComponent<AmbientAudio>();
            ambient.forest = Clip("forest_loop");
            ambient.bike = bike;

            // ---------- интерфейс ----------
            var manager = new GameObject("FreeRide").AddComponent<FreeRideManager>();
            manager.bike = bike;
            BuildUI(manager, bike.input);

            // ---------- сохранение ----------
            EditorSceneManager.SaveScene(scene, ScenePath);

            // ---------- главное меню (вторая сцена) и порядок сцен в сборке: меню первым ----------
            BuildMenuScene(mat, config, groundPhys, skin);
            SetBuildScenes(MenuScenePath, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("[Bike] Созданы сцены: главное меню " + MenuScenePath + " (открыта) и игра " + ScenePath + ". Нажмите Play. " +
                      "Controls: W/S/A/D or arrows, Space — jump, + / − — gears, R — restart, Esc — menu. Phone build: Bike → Android.");
        }


        /// <summary>Общие ассеты обеих сцен: материал, настройки байка (с переносом на новую версию), физика земли, скин.</summary>
        private static void PrepareShared(out Material mat, out BikeConfig config, out PhysicsMaterial2D groundPhys, out BikeSkin skin)
        {
            EnsureFolder(Root + "/Scenes");
            EnsureFolder(Root + "/Materials");
            EnsureFolder(Root + "/Settings");

            mat = GetOrCreateMaterial();
            config = GetOrCreateAsset<BikeConfig>(ConfigPath);
            if (config.version < BikeConfig.CurrentVersion)
            {
                // старый ассет из прошлой версии: переносим новые значения по умолчанию (масса гонщика, гравитация и т.д.)
                var fresh = ScriptableObject.CreateInstance<BikeConfig>();
                EditorUtility.CopySerialized(fresh, config);
                Object.DestroyImmediate(fresh);
                EditorUtility.SetDirty(config);
                Debug.Log("[Bike] BikeConfig_Default обновлён до новой физики (версия " + BikeConfig.CurrentVersion + ").");
            }
            groundPhys = GetOrCreateGroundPhysics();
            FixSpriteImports();
            skin = GetOrCreateSkin();
            _models = CreateBikeModels(config, skin);
        }

        /// <summary>
        /// Байки магазина. Хардтейл — копия настроек по умолчанию с отличиями: тяжелее (алюминий, ~15 кг), вилка 100 мм,
        /// сзади пружинит только шина (жёстко, ход 2.5 см), 8 скоростей (кассета 11–32), рама трескается от удара как с 5 м. Пересоздаётся при новой версии физики.
        /// </summary>
        private static BikeChoice.Model[] CreateBikeModels(BikeConfig config, BikeSkin skin)
        {
            var ht = GetOrCreateAsset<BikeConfig>(HardtailConfigPath);
            if (ht.version < BikeConfig.CurrentVersion || ht.rearSuspensionTravel <= 0f)
            {
                EditorUtility.CopySerialized(config, ht);
                ht.frameMass = 15f;
                ht.suspensionTravel = 0.10f;
                ht.suspensionFrequency = 9f;                     // вилка 100 мм — чуть жёстче (просадка ~30%)
                ht.breakFallHeight = 5f;                         // алюминиевый хардтейл трескается от удара, как при падении с 5 м
                ht.rearSuspensionFrequency = 18f;
                ht.rearSuspensionTravel = 0.025f;
                ht.cassette = new[] { 32, 28, 24, 21, 18, 15, 13, 11 };
                ht.startGear = 4;
                ht.version = BikeConfig.CurrentVersion;
                EditorUtility.SetDirty(ht);
            }
            bool fresh = AssetDatabase.LoadAssetAtPath<BikeSkin>(HardtailSkinPath) == null;
            var hs = GetOrCreateAsset<BikeSkin>(HardtailSkinPath);
            // гонщик — тот же, байк — свои спрайты (ht_*)
            hs.torso = skin.torso; hs.pelvis = skin.pelvis; hs.thigh = skin.thigh; hs.shin = skin.shin; hs.leg = skin.leg; hs.arm = skin.arm;
            hs.upperArm = skin.upperArm; hs.forearm = skin.forearm; hs.shoe = skin.shoe; hs.head = skin.head;
            hs.wheel = S("ht_wheel") ?? skin.wheel;
            hs.frame = S("ht_frame_base") != null ? L("ht_frame") : skin.frame;
            hs.frameCrack = S("ht_frame_crack") ?? skin.frameCrack;
            hs.frameRear = S("ht_frame_rear_base") != null ? L("ht_frame_rear") : skin.frameRear;
            hs.frameFront = S("ht_frame_front_base") != null ? L("ht_frame_front") : skin.frameFront;
            hs.swingarm = S("ht_swingarm_base") != null ? L("ht_swingarm") : skin.swingarm;
            hs.forkUpper = S("ht_fork_upper") ?? skin.forkUpper;
            hs.forkLower = S("ht_fork_lower") ?? skin.forkLower;
            hs.crankNear = S("ht_crank_near") ?? skin.crankNear;
            hs.crankFar = S("ht_crank_far") ?? skin.crankFar;
            if (fresh)
            {
                hs.frameColor = new Color(0.12f, 0.12f, 0.13f);     // матовый чёрный
                hs.jerseyColor = skin.jerseyColor; hs.shortsColor = skin.shortsColor; hs.helmetColor = skin.helmetColor;
                hs.farSideShade = skin.farSideShade; hs.lookVersion = skin.lookVersion;
            }
            if (hs.lookVersion < 4) { hs.helmetColor = skin.helmetColor; hs.lookVersion = 4; }   // новый шлем — матовый чёрный и на хардтейле
            EditorUtility.SetDirty(hs);
            return new[]
            {
                new BikeChoice.Model { displayName = "Trail 140", description = "Full suspension · 140 mm · 11 speeds",
                                       config = config, skin = skin, preview = MenuSprite("menu_bike_trail") },
                new BikeChoice.Model { displayName = "Welt Rocket 2.0 HD", description = "Hardtail · 100 mm fork · 8 speeds",
                                       config = ht, skin = hs, preview = MenuSprite("menu_bike_hardtail") },
            };
        }

        /// <summary>Камера и фон: вечер в горах, 6 слоёв параллакса.</summary>
        /// <summary>
        /// Атмосфера кадра (пост-обработка URP, общий профиль Settings/PostFX_Atmosphere): мягкое свечение светлого неба и
        /// бликов (Bloom), затемнение углов (Vignette), чуть больше контраста и меньше «мультяшной» насыщенности, холодные тени и
        /// тёплые света (Split Toning), лёгкое зерно плёнки. Действует на мир, не на интерфейс.
        /// </summary>
        private static void AddAtmospherePostFx(GameObject camGo, Camera cam)
        {
            const string path = Root + "/Settings/PostFX_Atmosphere.asset";
            var profile = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.VolumeProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }
            foreach (var c in profile.components.ToList())                       // пересоздаём настройки (обновление проекта)
            {
                profile.components.Remove(c);
                Object.DestroyImmediate(c, true);
            }
            T Add<T>() where T : UnityEngine.Rendering.VolumeComponent
            {
                T c = profile.Add<T>(true);
                c.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(c, profile);
                return c;
            }
            var bloom = Add<UnityEngine.Rendering.Universal.Bloom>();
            bloom.threshold.Override(0.9f); bloom.intensity.Override(0.35f); bloom.scatter.Override(0.7f);
            bloom.tint.Override(new Color(1f, 0.93f, 0.86f));
            var vignette = Add<UnityEngine.Rendering.Universal.Vignette>();
            vignette.intensity.Override(0.3f); vignette.smoothness.Override(0.5f); vignette.color.Override(new Color(0.05f, 0.05f, 0.09f));
            var adjust = Add<UnityEngine.Rendering.Universal.ColorAdjustments>();
            adjust.contrast.Override(10f); adjust.saturation.Override(-8f); adjust.postExposure.Override(0.05f);
            var split = Add<UnityEngine.Rendering.Universal.SplitToning>();
            split.shadows.Override(new Color(0.45f, 0.49f, 0.57f)); split.highlights.Override(new Color(0.57f, 0.52f, 0.46f));
            var grain = Add<UnityEngine.Rendering.Universal.FilmGrain>();
            grain.type.Override(UnityEngine.Rendering.Universal.FilmGrainLookup.Thin1); grain.intensity.Override(0.12f); grain.response.Override(0.8f);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            var volume = new GameObject("PostFX").AddComponent<UnityEngine.Rendering.Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = profile;
            UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(cam).renderPostProcessing = true;
        }

        private static GameObject CreateCameraAndBackground(Material mat)
        {
            // ---------- камера ----------
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 4.2f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            camGo.transform.position = new Vector3(4f, 1.5f, -10f);
            camGo.AddComponent<AudioListener>();
            AddAtmospherePostFx(camGo, cam);

            // ---------- фон: вечер в горах, 6 слоёв параллакса ----------
            var bgRoot = new GameObject("Background").transform;
            //            картинка                follow X  follow Y  центр   ветер  заливка снизу                      порядок
            var sky = AddLayer(bgRoot, camGo.transform, mat, "bg_sky",  0.985f, 1.00f,  1.0f,  0f,    new Color(0.99f, 0.84f, 0.67f), -100);
            Color skyTop = new Color(36f / 255f, 45f / 255f, 86f / 255f);          // цвет верха неба
            if (sky != null) sky.fillTopColor = skyTop;                             // при отдалении камеры выше неба — то же небо
            cam.backgroundColor = skyTop;

            // ---------- время суток: солнца не видно, в мире идут часы (секунда — минута); от них — небо, свет, звёзды ----------
            var dayNight = camGo.AddComponent<DayNightCycle>();
            dayNight.material = mat;
            dayNight.sky = sky;
            dayNight.layers = new[]
            {
                AddLayer(bgRoot, camGo.transform, mat, "bg_mountains_far",  0.95f,  1.00f,  1.77f, 0f,    new Color(0.89f, 0.69f, 0.64f), -90),
                AddLayer(bgRoot, camGo.transform, mat, "bg_mountains_mid",  0.90f,  0.98f,  1.77f, 0f,    new Color(0.83f, 0.66f, 0.62f), -80),
                Haze(AddLayer(bgRoot, camGo.transform, mat, "bg_forest_far", 0.80f, 0.95f, 1.77f, 0f, new Color(0.60f, 0.51f, 0.53f), -70),
                     new Color(0.72f, 0.61f, 0.60f)),
                AddLayer(bgRoot, camGo.transform, mat, "bg_fog",            0.72f,  0.93f, -0.96f, 0.15f, Color.clear,                     -65),
                Haze(AddLayer(bgRoot, camGo.transform, mat, "bg_forest_near", 0.60f, 0.90f, 1.77f, 0f, new Color(0.15f, 0.19f, 0.20f), -60),
                     new Color(0.55f, 0.49f, 0.52f)),                       // низ ближнего леса — в дымку долины, а не тёмно-зелёный
            };
            AddBackgroundThemes(camGo, sky, dayNight.layers);
            return camGo;
        }

        [System.Serializable]
        private class ThemeJson
        {
            public string name;
            public float[] skyFill, skyTop, farFill, midFill, forestFarFill, forestFarHaze, forestNearFill, forestNearHaze;
        }

        private static Color C(float[] a) => a != null && a.Length >= 3 ? new Color(a[0], a[1], a[2], 1f) : Color.clear;

        /// <summary>
        /// Фоны на выбор (случайный при каждом запуске сцены): родной (Альпы на закате) — как настроены слои, и темы из
        /// Art/Background/&lt;тема&gt; (картинки + theme.json с цветами заливок, Tools/art/environment/bg_themes.py).
        /// </summary>
        private static void AddBackgroundThemes(GameObject camGo, ParallaxLayer sky, ParallaxLayer[] layers)
        {
            var bt = camGo.AddComponent<BackgroundThemes>();
            bt.sky = sky; bt.mountainsFar = layers[0]; bt.mountainsMid = layers[1]; bt.forestFar = layers[2]; bt.fog = layers[3]; bt.forestNear = layers[4];
            var list = new List<BackgroundThemes.Theme>();
            if (sky != null && layers.All(l => l != null))
                list.Add(LocationLook(new BackgroundThemes.Theme
                {
                    name = "alpine", sky = sky.sprite, mountainsFar = layers[0].sprite, mountainsMid = layers[1].sprite,
                    forestFar = layers[2].sprite, fog = layers[3].sprite, forestNear = layers[4].sprite,
                    skyFill = sky.fillColor, skyTop = sky.fillTopColor, farFill = layers[0].fillColor, midFill = layers[1].fillColor,
                    forestFarFill = layers[2].fillColor, forestFarHaze = layers[2].fillHazeColor,
                    forestNearFill = layers[4].fillColor, forestNearHaze = layers[4].fillHazeColor,
                }));
            foreach (string name in new[] { "canyon", "misty", "autumn", "winter" })
            {
                string dir = $"{EnvArt}/Background/{name}";
                var json = AssetDatabase.LoadAssetAtPath<TextAsset>(dir + "/theme.json");
                Sprite Sp(string n) => AssetDatabase.LoadAssetAtPath<Sprite>($"{dir}/{n}.png");
                if (json == null || Sp("bg_sky") == null) { Debug.LogWarning("[Bike] Нет фона " + name + " (" + dir + ")"); continue; }
                var j = JsonUtility.FromJson<ThemeJson>(json.text);
                list.Add(LocationLook(new BackgroundThemes.Theme
                {
                    name = name, sky = Sp("bg_sky"), mountainsFar = Sp("bg_mountains_far"), mountainsMid = Sp("bg_mountains_mid"),
                    forestFar = Sp("bg_forest_far"), fog = Sp("bg_fog"), forestNear = Sp("bg_forest_near"),
                    skyFill = C(j.skyFill), skyTop = C(j.skyTop), farFill = C(j.farFill), midFill = C(j.midFill),
                    forestFarFill = C(j.forestFarFill), forestFarHaze = C(j.forestFarHaze),
                    forestNearFill = C(j.forestNearFill), forestNearHaze = C(j.forestNearHaze),
                }));
            }
            bt.themes = list.ToArray();
        }

        /// <summary>
        /// Земля, трава, кусты и погода локации (картинки — Art/Ground/&lt;локация&gt;, Art/Grass/&lt;локация&gt;,
        /// Art/Grass/Tufts/&lt;локация&gt;, генератор Tools/art/environment/locations_art.py). Альпы — родные текстуры трассы.
        /// </summary>
        private static BackgroundThemes.Theme LocationLook(BackgroundThemes.Theme t)
        {
            string n = t.name;
            if (n != "alpine")
            {
                t.ground = AssetDatabase.LoadAssetAtPath<Texture2D>($"{EnvArt}/Ground/{n}/ground.png");
                t.topsoil = AssetDatabase.LoadAssetAtPath<Texture2D>($"{EnvArt}/Ground/{n}/ground_top.png");
                t.grassBack = AssetDatabase.LoadAssetAtPath<Texture2D>($"{EnvArt}/Grass/{n}/grass_back.png");
                t.grassFront = AssetDatabase.LoadAssetAtPath<Texture2D>($"{EnvArt}/Grass/{n}/grass_front.png");
                var tl = new List<Sprite>();
                for (int i = 1; i <= 6; i++)
                {
                    var sp = AssetDatabase.LoadAssetAtPath<Sprite>($"{EnvArt}/Grass/Tufts/{n}/tuft_{i}.png");
                    if (sp != null) tl.Add(sp);
                }
                if (tl.Count == 6) t.tufts = tl.ToArray();
                else if (tl.Count > 0) { t.tufts = tl.ToArray(); Debug.LogWarning("[Bike] Не все кусты локации " + n); }
                if (t.ground == null || t.grassBack == null) Debug.LogWarning("[Bike] Нет текстур земли локации " + n + " (Art/Ground/" + n + ")");
            }
            switch (n)
            {
                case "alpine":          // Альпы: пушинки одуванчиков, пылинки в закатном свете
                    t.weather = LocationWeather.Kind.Fluff; t.moteRate = 14f;
                    break;
                case "canyon":          // каньон: красная глина, редкая сухая трава, полынь, опунция; пыль и перекати-поле
                    t.weather = LocationWeather.Kind.Dust; t.moteRate = 26f; t.moteColor = new Color(1f, 0.8f, 0.6f, 0.5f);
                    t.groundTint = new Color(0.86f, 0.78f, 0.74f); t.rockTint = new Color(1f, 0.72f, 0.58f);
                    t.tuftsPer10m = 3.2f; t.tuftSway = new[] { 0.3f, 1f, 0f, 0f, 0.3f, 1f };
                    break;
                case "misty":           // туман: сырая тёмная земля, мох, папоротники; клочья тумана и морось
                    t.weather = LocationWeather.Kind.Mist; t.moteRate = 0f;
                    t.groundTint = new Color(0.74f, 0.76f, 0.76f); t.rockTint = new Color(0.78f, 0.84f, 0.84f);
                    t.tuftsPer10m = 5f; t.tuftSway = new[] { 0.7f, 0f, 0.4f, 1f, 0.7f, 0.5f };
                    break;
                case "autumn":          // осень: листопад, листья в траве и в перегное, кучки листьев, грибы
                    t.weather = LocationWeather.Kind.Leaves; t.moteRate = 8f; t.moteColor = new Color(1f, 0.88f, 0.66f, 0.5f);
                    t.tuftsPer10m = 5f; t.tuftSway = new[] { 0f, 0.4f, 1f, 1f, 0.4f, 1f };
                    break;
                case "winter":          // зима: снег вместо травы (не качается), сугробы, снегопад и позёмка
                    t.weather = LocationWeather.Kind.Snow; t.moteRate = 0f;
                    t.groundTint = new Color(0.8f, 0.8f, 0.86f); t.rockTint = new Color(0.86f, 0.9f, 1f);
                    t.grassBackHeight = 0.9f; t.grassFrontHeight = 0.2f; t.grassSway = 0f; t.grip = 0.5f;               // снег: сцепление вдвое меньше
                    t.tuftsPer10m = 3f; t.tuftSway = new[] { 0f, 0.6f, 0.3f, 0f, 0.25f, 0.6f };
                    break;
            }
            return t;
        }

        /// <summary>Связывает локации со сценой: трасса, пылинки и погода (листопад, снег, пыль, туман).</summary>
        private static void WireLocations(GameObject camGo, TerrainGenerator terrain, AmbientMotes motes, Material mat, BackgroundThemes.Pick pick)
        {
            var bt = camGo.GetComponent<BackgroundThemes>();
            if (bt == null) return;
            bt.pick = pick;
            bt.terrain = terrain;
            bt.motes = motes;
            var w = camGo.AddComponent<LocationWeather>();
            w.terrain = terrain;
            w.leafMaterial = GetOrCreateTexMaterial("Leaf", EnvArt + "/FX/leaf_sheet.png");
            w.snowMaterial = GetOrCreateTexMaterial("Snowflake", EnvArt + "/FX/snowflake.png");
            w.fluffMaterial = GetOrCreateTexMaterial("Fluff", EnvArt + "/FX/fluff.png");
            w.dustMaterial = GetOrCreateTexMaterial("Dust", EnvArt + "/FX/dust.png");
            w.fogMaterial = GetOrCreateTexMaterial("FogWisp", EnvArt + "/FX/fog_wisp.png");
            w.dropMaterial = GetOrCreateTexMaterial("Mote", EnvArt + "/FX/mote.png");
            w.tumbleweed = AssetDatabase.LoadAssetAtPath<Sprite>(EnvArt + "/FX/tumbleweed.png");
            w.spriteMaterial = mat;
            bt.weather = w;
        }

        /// <summary>Трасса: почва, перегной, трава, дерево, камни.</summary>
        private static TerrainGenerator CreateTerrain(Material mat, PhysicsMaterial2D groundPhys)
        {
            // ---------- трасса: почва, перегной, трава ----------
            var terrain = new GameObject("Terrain").AddComponent<TerrainGenerator>();
            terrain.material = mat;
            terrain.groundPhysics = groundPhys;
            terrain.groundMaterial = GetOrCreateTexMaterial("Ground", EnvArt + "/Ground/ground.png");
            terrain.topsoilMaterial = GetOrCreateTexMaterial("Topsoil", EnvArt + "/Ground/ground_top.png");
            terrain.grassBackMaterial = GetOrCreateTexMaterial("GrassBack", EnvArt + "/Grass/grass_back.png");
            terrain.grassFrontMaterial = GetOrCreateTexMaterial("GrassFront", EnvArt + "/Grass/grass_front.png");
            terrain.tuftMaterial = mat;
            terrain.groundTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(EnvArt + "/Ground/ground.png");
            terrain.topsoilTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(EnvArt + "/Ground/ground_top.png");
            terrain.grassBackTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(EnvArt + "/Grass/grass_back.png");
            terrain.grassFrontTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(EnvArt + "/Grass/grass_front.png");
            // деревянные кикеры и дропы (Rampage): доски настила и опоры
            terrain.woodMaterial = GetOrCreateTexMaterial("Wood", EnvArt + "/Wood/wood_deck.png");
            terrain.woodDeckTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(EnvArt + "/Wood/wood_deck.png");
            terrain.woodPostTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(EnvArt + "/Wood/wood_post.png");
            var tufts = new List<Sprite>();
            for (int i = 1; i <= 6; i++)
            {
                var sp = AssetDatabase.LoadAssetAtPath<Sprite>($"{EnvArt}/Grass/Tufts/grass_tuft_{i}.png");
                if (sp != null) tufts.Add(sp);
            }
            terrain.tufts = tufts.ToArray();
            var rockSprites = new List<Sprite>();                       // камни на обрывах дропов и каменистых участках
            for (int i = 1; i <= 5; i++)
            {
                var sp = AssetDatabase.LoadAssetAtPath<Sprite>($"{EnvArt}/Rocks/rock_{i}.png");
                if (sp != null) rockSprites.Add(sp);
            }
            terrain.rocks = rockSprites.ToArray();

            return terrain;
        }

        // =====================================================================================
        private static BikeController2D BuildBike(Material mat, BikeConfig config, BikeSkin skin, Vector2 position)
        {
            var root = new GameObject("Bike");
            root.transform.position = position;
            float axleY = WheelRadius + 0.02f;

            // рама
            var frameGo = new GameObject("Frame");
            frameGo.transform.SetParent(root.transform, false);
            frameGo.transform.localPosition = new Vector3(0f, axleY);
            var frame = AddBody(frameGo, config.frameMass);
            var frameCol = frameGo.AddComponent<BoxCollider2D>();
            frameCol.offset = new Vector2(0.02f, 0.42f);
            frameCol.size = new Vector2(0.9f, 0.25f);

            // голова = датчик падения
            var headGo = new GameObject("Head");
            headGo.transform.SetParent(frameGo.transform, false);
            headGo.transform.localPosition = new Vector3(0.106f, 1.187f); // нейтральная поза; дальше двигает BikeVisual
            var head = headGo.AddComponent<CircleCollider2D>();
            head.isTrigger = true;
            head.radius = 0.15f; // фулфейс-шлем
            var crash = headGo.AddComponent<CrashDetector>();

            // колёса
            Rigidbody2D rear = BuildWheel(root.transform, "RearWheel", new Vector2(-WheelBase / 2f, axleY), config);
            Rigidbody2D front = BuildWheel(root.transform, "FrontWheel", new Vector2(WheelBase / 2f, axleY), config);

            WheelJoint2D rearJoint = AddWheelJoint(frameGo, rear, new Vector2(-WheelBase / 2f, 0f), config);
            WheelJoint2D frontJoint = AddWheelJoint(frameGo, front, new Vector2(WheelBase / 2f, 0f), config);

            // тело гонщика: отдельная масса без коллайдеров и без вращения; «мышцы» считает RiderBody
            var riderGo = new GameObject("RiderBody");
            riderGo.transform.SetParent(root.transform, false);
            Vector2 com0 = new Vector2(-0.33f + 0.224f, 0.745f + 0.012f);   // центр масс в нейтральной стойке
            riderGo.transform.localPosition = new Vector3(com0.x, axleY + com0.y);
            var riderRb = AddBody(riderGo, config.riderMass * config.massScale);
            riderRb.collisionDetectionMode = CollisionDetectionMode2D.Discrete;
            riderRb.freezeRotation = true;

            // логика
            var input = root.AddComponent<BikeInput>();
            var rider = root.AddComponent<RiderBody>();
            var bike = root.AddComponent<BikeController2D>();
            bike.config = config;
            bike.input = input;
            bike.rider = rider;
            rider.bike = bike;
            rider.input = input;
            rider.body = riderRb;
            bike.frame = frame;
            bike.rearWheel = rear;
            bike.frontWheel = front;
            bike.rearJoint = rearJoint;
            bike.frontJoint = frontJoint;
            crash.bike = bike;

            var choice = root.AddComponent<BikeChoice>();   // байк, выбранный в магазине (подставляется до сборки физики и спрайтов)
            choice.models = _models ?? new BikeChoice.Model[0];

            var visual = root.AddComponent<BikeVisual>();
            visual.bike = bike;
            visual.input = input;
            visual.skin = skin;
            visual.material = mat;
            visual.headTrigger = headGo.transform;
            visual.rider = rider;
            return bike;
        }

        private static Rigidbody2D AddBody(GameObject go, float mass)
        {
            var rb = go.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Dynamic;
            rb.mass = mass;
            rb.interpolation = RigidbodyInterpolation2D.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            return rb;
        }

        private static Rigidbody2D BuildWheel(Transform parent, string name, Vector2 localPos, BikeConfig config)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var rb = AddBody(go, config.wheelMass);
            var col = go.AddComponent<CircleCollider2D>();
            col.radius = WheelRadius;
            return rb;
        }

        private static WheelJoint2D AddWheelJoint(GameObject frame, Rigidbody2D wheel, Vector2 anchor, BikeConfig config)
        {
            var j = frame.AddComponent<WheelJoint2D>();
            j.connectedBody = wheel;
            j.autoConfigureConnectedAnchor = false;
            j.anchor = anchor;
            j.connectedAnchor = Vector2.zero;
            j.enableCollision = false;
            JointSuspension2D s = j.suspension;
            s.angle = 90f;
            s.frequency = config.suspensionFrequency;
            s.dampingRatio = config.suspensionDamping;
            j.suspension = s;
            j.useMotor = false;
            return j;
        }

        // =====================================================================================
        private static void BuildUI(FreeRideManager manager, BikeInput input)
        {
            var canvasGo = new GameObject("Canvas") { layer = 5 };
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<GraphicRaycaster>();

            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            es.AddComponent<StandaloneInputModule>();
#endif
            RectTransform root = canvasGo.GetComponent<RectTransform>();
            AddLightTint(root);
            // мягкое затемнение по краям экрана (виньетка) — взгляд в центр, картинка «киношнее»
            var vignette = MakeRect(root, "Vignette", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero).gameObject.AddComponent<Image>();
            vignette.sprite = UiSprite("vignette");
            vignette.color = vignette.sprite != null ? new Color(1f, 1f, 1f, 0.85f) : Color.clear;
            vignette.raycastTarget = false;
            // всё, кроме затемнения при падении, — внутри «безопасной зоны» (вырез камеры, закруглённые углы телефона)
            var c = MakeRect(root, "SafeArea", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            c.gameObject.AddComponent<SafeArea>();

            // HUD
            manager.distanceText = MakeText(c, "Distance", new Vector2(0.5f, 1f), new Vector2(0, -60), new Vector2(600, 90), 72, TextAnchor.MiddleCenter, "0 m");
            manager.speedText = MakeText(c, "Speed", new Vector2(0.5f, 1f), new Vector2(0, -130), new Vector2(400, 50), 38, TextAnchor.MiddleCenter, "0 km/h");
            // очки за трюки — справа вверху (как в Mountain Bike Xtreme), надпись трюка — по центру над байком
            manager.scoreText = MakeText(c, "Score", new Vector2(1f, 1f), new Vector2(-150, -45), new Vector2(240, 60), 48, TextAnchor.MiddleRight, "0");
            manager.trickText = MakeText(c, "Trick", new Vector2(0.5f, 1f), new Vector2(0, -250), new Vector2(900, 70), 52, TextAnchor.MiddleCenter, "");
            manager.bestText = MakeText(c, "Best", new Vector2(0f, 1f), new Vector2(40, -45), new Vector2(500, 50), 36, TextAnchor.MiddleLeft, "Record: 0 m");
            manager.bestText.rectTransform.pivot = new Vector2(0f, 0.5f);
            manager.gearText = MakeText(c, "Gear", new Vector2(0.5f, 1f), new Vector2(0, -180), new Vector2(500, 50), 34, TextAnchor.MiddleCenter, "Gear 5/11");
            // часы мира (время суток) — справа вверху под очками
            var clock = MakeText(c, "Clock", new Vector2(1f, 1f), new Vector2(-150, -105), new Vector2(240, 50), 36, TextAnchor.MiddleRight, "08:00");
            var clockCam = GameObject.Find("Main Camera");
            if (clockCam != null && clockCam.GetComponent<DayNightCycle>() != null) clockCam.GetComponent<DayNightCycle>().clockText = clock;
            manager.hintText = MakeText(c, "Hint", new Vector2(0.5f, 0f), new Vector2(0, 60), new Vector2(1000, 50), 32, TextAnchor.MiddleCenter, "");

            // Экранные кнопки (картинки — Art/UI): слева наклон, справа тормоз и газ, над ними передачи
            var touch = MakeRect(c, "TouchControls", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            input.leanBackButton = MakeButton(touch, "LeanBack", new Vector2(0f, 0f), new Vector2(165, 165), "BACK", UiSprite("btn_lean_back"), 250f);
            input.leanForwardButton = MakeButton(touch, "LeanForward", new Vector2(0f, 0f), new Vector2(435, 165), "FORWARD", UiSprite("btn_lean_forward"), 250f);
            input.brakeButton = MakeButton(touch, "Brake", new Vector2(1f, 0f), new Vector2(-435, 165), "BRAKE", UiSprite("btn_brake"), 250f);
            input.pedalButton = MakeButton(touch, "Pedal", new Vector2(1f, 0f), new Vector2(-165, 165), "GAS", UiSprite("btn_gas"), 250f);
            input.gearDownButton = MakeButton(touch, "GearDown", new Vector2(1f, 0f), new Vector2(-435, 425), "−", UiSprite("btn_gear_down"), 170f, 80);
            input.gearUpButton = MakeButton(touch, "GearUp", new Vector2(1f, 0f), new Vector2(-165, 425), "+", UiSprite("btn_gear_up"), 170f, 80);
            // прыжок (банни-хоп) — над кнопками наклона, между ними
            input.jumpButton = MakeButton(touch, "Jump", new Vector2(0f, 0f), new Vector2(300, 420), "JUMP", UiSprite("btn_jump"), 190f);
            // скольжение пальцем: прижал ГАЗ и сдвинул на ТОРМОЗ — работают оба (газ держится, пока палец прижат);
            // с тормоза на газ или между наклонами — кнопка переключается
            input.pedalButton.slideIn = input.pedalButton.keepWhileDragging = true;
            input.brakeButton.slideIn = true;
            input.leanBackButton.slideIn = input.leanForwardButton.slideIn = true;
            manager.gearTouchText = MakeText(touch, "GearNumber", new Vector2(1f, 0f), new Vector2(-300, 425), new Vector2(100, 100), 64, TextAnchor.MiddleCenter, "5");
            manager.touchControls = touch.gameObject;

            // Панель падения (затемняет весь экран)
            var panel = MakeRect(root, "CrashPanel", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var bg = panel.gameObject.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.55f);
            manager.crashText = MakeText(panel, "CrashText", new Vector2(0.5f, 0.5f), new Vector2(0, 90), new Vector2(1200, 220), 64, TextAnchor.MiddleCenter, "Crash!");

            var btnRect = MakeRect(panel, "RestartButton", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -110), new Vector2(460, 120));
            var btnImg = btnRect.gameObject.AddComponent<Image>();
            Sprite restart = UiSprite("btn_restart");
            if (restart != null) { btnImg.sprite = restart; btnImg.color = Color.white; }
            else btnImg.color = new Color(1f, 0.78f, 0.25f);
            manager.restartButton = btnRect.gameObject.AddComponent<Button>();
            var label = MakeText(btnRect, "Label", new Vector2(0.5f, 0.5f), new Vector2(restart != null ? 25 : 0, 2), new Vector2(460, 120), 46, TextAnchor.MiddleCenter, "RESTART");
            label.color = new Color(0.24f, 0.1f, 0.02f);
            label.GetComponent<Outline>().enabled = false;
            // «Меню» — в главное меню
            var menuRect = MakeRect(panel, "MenuButton", new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, -250), new Vector2(420, 98));
            var menuImg = menuRect.gameObject.AddComponent<Image>();
            menuImg.sprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{EnvArt}/UI/Menu/menu_btn_menu.png");
            menuImg.preserveAspect = true;
            manager.menuButton = menuRect.gameObject.AddComponent<Button>();
            menuRect.gameObject.AddComponent<ButtonPress>().sway = 0f;
            manager.crashPanel = panel.gameObject;
        }

        /// <summary>Засветка всей сцены по времени суток (тёплая на рассвете и закате, тёмно-синяя ночью) — поверх сцены, под интерфейсом.</summary>
        private static Image AddLightTint(RectTransform root)
        {
            var img = MakeRect(root, "DayNightTint", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero).gameObject.AddComponent<Image>();
            img.color = Color.clear;
            img.raycastTarget = false;
            var camGo = GameObject.Find("Main Camera");
            var dayNight = camGo != null ? camGo.GetComponent<DayNightCycle>() : null;
            if (dayNight != null) dayNight.screenTint = img;
            return img;
        }

        private static AudioClip Clip(string name)
        {
            var c = AssetDatabase.LoadAssetAtPath<AudioClip>($"{Root}/Audio/{name}.wav");
            if (c == null) Debug.LogWarning("[Bike] Нет звука " + name + " (Assets/_Project/Audio)");
            return c;
        }

        private static Sprite UiSprite(string name) => AssetDatabase.LoadAssetAtPath<Sprite>($"{EnvArt}/UI/{name}.png");

        private static RectTransform MakeRect(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform)) { layer = 5 };
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        private static Text MakeText(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, int fontSize, TextAnchor align, string value)
        {
            var rt = MakeRect(parent, name, anchor, anchor, pos, size);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = BuiltinFont();
            t.fontSize = fontSize;
            t.fontStyle = FontStyle.Bold;
            t.alignment = align;
            t.color = Color.white;
            t.text = value;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            var o = rt.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(0f, 0f, 0f, 0.8f);
            o.effectDistance = new Vector2(2f, -2f);
            return t;
        }

        /// <summary>Экранная кнопка: картинка из Art/UI, а если её нет — полупрозрачный квадрат с надписью.</summary>
        private static TouchButton MakeButton(Transform parent, string name, Vector2 anchor, Vector2 pos, string label, Sprite sprite,
                                              float size = 230f, int fontSize = 36)
        {
            var rt = MakeRect(parent, name, anchor, anchor, pos, new Vector2(size, size));
            var img = rt.gameObject.AddComponent<Image>();
            var button = rt.gameObject.AddComponent<TouchButton>();
            if (sprite != null)
            {
                img.sprite = sprite;
                img.preserveAspect = true;
                img.color = new Color(button.idleBrightness, button.idleBrightness, button.idleBrightness, button.idleAlpha);
            }
            else
            {
                img.color = new Color(1f, 1f, 1f, 0.35f);
                button.idleAlpha = 0.35f; button.pressedAlpha = 0.7f; button.idleBrightness = 1f;
                var text = MakeText(rt, "Label", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size), fontSize, TextAnchor.MiddleCenter, label);
                text.raycastTarget = false;
            }
            return button;
        }

        private static Font BuiltinFont()
        {
#if UNITY_2022_2_OR_NEWER
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
        }

        // =====================================================================================
        /// <summary>Все картинки из Art/Bike → спрайты с PPU 256 и центром в середине (на случай, если импорт прошёл до компиляции скриптов).</summary>
        private static void FixSpriteImports()
        {
            FixImportsIn(EnvArt);
            // проверка: спрайты байка должны загрузиться, иначе байк будет невидимым
            if (S("frame_base") == null || S("wheel") == null)
            {
                Debug.LogWarning("[Bike] Спрайты байка не загрузились — принудительно переимпортирую папку Art.");
                AssetDatabase.ImportAsset(EnvArt, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
                if (S("frame_base") == null)
                    Debug.LogError("[Bike] Спрайты байка так и не загрузились: выделите папку Assets/_Project/Art → ПКМ → Reimport и снова запустите Bike → Создать сцену Free Ride.");
            }
        }

        private static void FixImportsIn(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return;
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is TextureImporter ti && ArtSpriteImporter.Apply(ti))
                    ti.SaveAndReimport();
            }
        }

        /// <summary>Создаёт (или обновляет спрайты в) BikeSkin_Default. Цвета, которые вы поменяли, не трогает.</summary>
        private static BikeSkin GetOrCreateSkin()
        {
            BikeSkin skin = GetOrCreateAsset<BikeSkin>(SkinPath);
            skin.wheel = S("wheel");
            skin.frame = L("frame");
            skin.frameCrack = S("frame_crack");
            skin.frameRear = L("frame_rear");
            skin.frameFront = L("frame_front");
            skin.swingarm = L("swingarm");
            skin.forkUpper = S("fork_upper");
            skin.forkLower = S("fork_lower");
            skin.crankNear = S("crank_near");
            skin.crankFar = S("crank_far");
            skin.torso = L("torso");
            skin.pelvis = L("pelvis");           // таз в штанах — плавный переход от тела к ногам
            skin.thigh = L("thigh");
            skin.shin = L("shin");

            skin.leg = L("leg");                 // цельные нога и рука — гнутся в суставах (SkinnedLimb)
            skin.arm = L("arm");
            skin.upperArm = L("upperarm");
            skin.forearm = L("forearm");
            skin.shoe = L("shoe");
            skin.head = L("head");
            skin.shoe.paintLayer = null;
            if (skin.lookVersion < 3)
            {
                // гонщик v6: новые цвета по умолчанию (оранжевое джерси, тёмные штаны, белый шлем)
                skin.jerseyColor = new Color(0.85f, 0.42f, 0.16f);
                skin.shortsColor = new Color(0.2f, 0.21f, 0.25f);
                skin.helmetColor = new Color(0.93f, 0.93f, 0.95f);
                skin.lookVersion = 3;
            }
            if (skin.lookVersion < 4)
            {
                // шлем v11 (эндуро-фулфейс со съёмной дугой, как на образце) — матовый чёрный
                skin.helmetColor = new Color(0.15f, 0.15f, 0.16f);
                skin.lookVersion = 4;
            }
            EditorUtility.SetDirty(skin);
            if (skin.wheel == null)
                Debug.LogError("[Bike] Не найдены спрайты в " + ArtFolder + ". Скопируйте папку Art из архива.");
            return skin;
        }

        private static Sprite S(string name) => AssetDatabase.LoadAssetAtPath<Sprite>($"{ArtFolder}/{name}.png");

        private static BikeSkin.Layered L(string prefix) => new BikeSkin.Layered
        {
            baseLayer = S(prefix + "_base"),
            paintLayer = S(prefix + "_paint"),
            topLayer = S(prefix + "_top"),
            lightUp = S(prefix + "_lu"),
            lightDown = S(prefix + "_ld"),
        };

        private static Material GetOrCreateMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat != null) return mat;

            Shader shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            mat = new Material(shader) { name = "VertexColorUnlit" };
            AssetDatabase.CreateAsset(mat, MaterialPath);
            return mat;
        }

        /// <summary>Материал с текстурой (тот же неосвещённый шейдер, цвет вершин — оттенок).</summary>
        private static Material GetOrCreateTexMaterial(string name, string texturePath)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (tex == null) { Debug.LogWarning("[Bike] Нет текстуры " + texturePath); return null; }
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(GetOrCreateMaterial().shader) { name = name };
                AssetDatabase.CreateAsset(m, path);
            }
            m.mainTexture = tex;
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
            EditorUtility.SetDirty(m);
            return m;
        }

        private static ParallaxLayer AddLayer(Transform parent, Transform cam, Material mat, string sprite, float followX, float followY,
                                     float offsetY, float drift, Color fill, int order)
        {
            var sp = AssetDatabase.LoadAssetAtPath<Sprite>($"{EnvArt}/Background/{sprite}.png");
            if (sp == null) { Debug.LogWarning("[Bike] Нет слоя фона " + sprite); return null; }
            var layer = new GameObject(sprite).AddComponent<ParallaxLayer>();
            layer.transform.SetParent(parent, false);
            layer.cameraTransform = cam;
            layer.sprite = sp;
            layer.material = mat;
            layer.followX = followX;
            layer.followY = followY;
            layer.offsetY = offsetY;
            layer.driftSpeed = drift;
            layer.fillColor = fill;
            layer.sortingOrder = order;
            return layer;
        }

        /// <summary>Заливка под слоем плавно уходит в дымку долины (а не сплошной цвет нижнего края).</summary>
        private static ParallaxLayer Haze(ParallaxLayer layer, Color haze)
        {
            if (layer != null) layer.fillHazeColor = haze;
            return layer;
        }

        private static PhysicsMaterial2D GetOrCreateGroundPhysics()
        {
            var pm = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(GroundPhysicsPath);
            if (pm != null) return pm;
            pm = new PhysicsMaterial2D("Ground") { friction = 0.9f, bounciness = 0f };
            AssetDatabase.CreateAsset(pm, GroundPhysicsPath);
            return pm;
        }

        private static T GetOrCreateAsset<T>(string path) where T : ScriptableObject
        {
            var a = AssetDatabase.LoadAssetAtPath<T>(path);
            if (a != null) return a;
            a = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(a, path);
            return a;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }

        internal static void AddSceneToBuild(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.Exists(s => s.path == path)) return;
            scenes.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}

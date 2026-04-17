using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem.UI;
#endif

namespace Sumo.Online
{
    public sealed class RuntimeMainMenuInstaller : MonoBehaviour
    {
        private const string RuntimeRootName = "SumoMainMenuRuntimeRoot";
        private const string CanvasName = "SumoMainMenuCanvas";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallIfNeeded()
        {
            if (IsDedicatedServerEnvironment())
            {
                return;
            }

            if (FindObjectOfType<MainMenuController>(true) != null)
            {
                return;
            }

            RuntimeMainMenuInstaller existingInstaller = FindObjectOfType<RuntimeMainMenuInstaller>(true);
            if (existingInstaller != null)
            {
                return;
            }

            GameObject root = new GameObject(RuntimeRootName);
            DontDestroyOnLoad(root);
            root.AddComponent<RuntimeMainMenuInstaller>();
        }

        private void Awake()
        {
            if (IsDedicatedServerEnvironment())
            {
                Destroy(gameObject);
                return;
            }

            DontDestroyOnLoad(gameObject);
            BuildRuntimeMainMenu();
        }

        private static bool IsDedicatedServerEnvironment()
        {
#if UNITY_SERVER
            return true;
#else
            if (Application.isBatchMode)
            {
                return true;
            }

            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-dedicatedServer", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "--dedicatedServer", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "-server", System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], "--server", System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
#endif
        }

        private void BuildRuntimeMainMenu()
        {
            EnsureEventSystem();

            Canvas canvas = CreateCanvas();
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            GameObject overlay = CreatePanel("Overlay", canvas.transform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Color(0f, 0f, 0f, 0.55f));

            GameObject mainPanel = CreatePanel("MainPanel", overlay.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Color(0.08f, 0.1f, 0.15f, 0.95f));
            SetSize(mainPanel, new Vector2(520f, 380f));
            AddVerticalLayout(mainPanel, 18, new RectOffset(36, 36, 36, 36), TextAnchor.MiddleCenter);

            CreateLabel("Title", mainPanel.transform, "SUMO", font, 56, TextAnchor.MiddleCenter, Color.white);
            CreateLabel("Subtitle", mainPanel.transform, "Dedicated Multiplayer", font, 24, TextAnchor.MiddleCenter, new Color(0.82f, 0.88f, 0.97f, 1f));

            Button multiplayerButton = CreateButton("MultiplayerButton", mainPanel.transform, "Multiplayer", font);
            SetButtonHeight(multiplayerButton, 64f);

            Button quitButton = CreateButton("QuitButton", mainPanel.transform, "Quit", font);
            SetButtonHeight(quitButton, 58f);

            GameObject multiplayerPanel = CreatePanel("MultiplayerPanel", overlay.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Color(0.08f, 0.1f, 0.15f, 0.95f));
            SetSize(multiplayerPanel, new Vector2(620f, 420f));
            AddVerticalLayout(multiplayerPanel, 14, new RectOffset(36, 36, 36, 36), TextAnchor.UpperCenter);

            CreateLabel("MpTitle", multiplayerPanel.transform, "Multiplayer", font, 44, TextAnchor.MiddleCenter, Color.white);
            Text statusText = CreateLabel("StatusText", multiplayerPanel.transform, "Idle", font, 28, TextAnchor.MiddleCenter, new Color(0.84f, 0.91f, 1f, 1f));

            Button findButton = CreateButton("FindGameButton", multiplayerPanel.transform, "Find Game", font);
            SetButtonHeight(findButton, 62f);

            Button cancelButton = CreateButton("CancelSearchButton", multiplayerPanel.transform, "Cancel Search", font);
            SetButtonHeight(cancelButton, 56f);

            Button backButton = CreateButton("BackButton", multiplayerPanel.transform, "Back", font);
            SetButtonHeight(backButton, 56f);

            MainMenuController mainMenuController = gameObject.AddComponent<MainMenuController>();
            ClientFusionConnector connector = gameObject.AddComponent<ClientFusionConnector>();
            MatchmakingClient matchmakingClient = gameObject.AddComponent<MatchmakingClient>();
            MultiplayerMenuController multiplayerMenuController = gameObject.AddComponent<MultiplayerMenuController>();

            BootstrapConfig bootstrapConfig = Resources.Load<BootstrapConfig>("BootstrapConfig");

            matchmakingClient.Configure(bootstrapConfig, connector, false);
            mainMenuController.Configure(mainPanel, multiplayerPanel);
            multiplayerMenuController.Configure(
                matchmakingClient,
                mainMenuController,
                findButton,
                cancelButton,
                backButton,
                statusText,
                canvas);

            multiplayerButton.onClick.AddListener(mainMenuController.OpenMultiplayer);
            quitButton.onClick.AddListener(mainMenuController.QuitGame);
        }

        private static void EnsureEventSystem()
        {
            if (FindObjectOfType<EventSystem>(true) != null)
            {
                return;
            }

            GameObject eventSystemObject = new GameObject("EventSystem");
            DontDestroyOnLoad(eventSystemObject);
            eventSystemObject.AddComponent<EventSystem>();

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            eventSystemObject.AddComponent<InputSystemUIInputModule>();
#else
            eventSystemObject.AddComponent<StandaloneInputModule>();
#endif
        }

        private static Canvas CreateCanvas()
        {
            GameObject canvasObject = new GameObject(CanvasName);
            DontDestroyOnLoad(canvasObject);

            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 999;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();

            return canvas;
        }

        private static GameObject CreatePanel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Color color)
        {
            GameObject panel = new GameObject(name, typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(parent, false);

            RectTransform rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;

            Image image = panel.GetComponent<Image>();
            image.color = color;

            return panel;
        }

        private static Text CreateLabel(string name, Transform parent, string value, Font font, int fontSize, TextAnchor anchor, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);

            Text text = go.GetComponent<Text>();
            text.text = value;
            text.font = font;
            text.fontSize = fontSize;
            text.alignment = anchor;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            LayoutElement layout = go.AddComponent<LayoutElement>();
            layout.preferredHeight = fontSize + 18f;

            return text;
        }

        private static Button CreateButton(string name, Transform parent, string caption, Font font)
        {
            GameObject buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);

            Image image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.17f, 0.38f, 0.57f, 1f);

            Button button = buttonObject.GetComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(0.17f, 0.38f, 0.57f, 1f);
            colors.highlightedColor = new Color(0.23f, 0.49f, 0.71f, 1f);
            colors.pressedColor = new Color(0.12f, 0.28f, 0.43f, 1f);
            colors.disabledColor = new Color(0.25f, 0.25f, 0.25f, 0.75f);
            button.colors = colors;

            GameObject textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(buttonObject.transform, false);

            RectTransform textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            Text label = textObject.GetComponent<Text>();
            label.text = caption;
            label.font = font;
            label.fontSize = 30;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;

            LayoutElement layout = buttonObject.AddComponent<LayoutElement>();
            layout.preferredHeight = 56f;

            return button;
        }

        private static void AddVerticalLayout(GameObject target, int spacing, RectOffset padding, TextAnchor childAlignment)
        {
            VerticalLayoutGroup layout = target.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = padding;
            layout.childAlignment = childAlignment;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            ContentSizeFitter fitter = target.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private static void SetButtonHeight(Button button, float height)
        {
            if (button == null)
            {
                return;
            }

            LayoutElement layout = button.GetComponent<LayoutElement>();
            if (layout != null)
            {
                layout.preferredHeight = height;
            }
        }

        private static void SetSize(GameObject target, Vector2 size)
        {
            RectTransform rect = target.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.sizeDelta = size;
            }
        }
    }
}

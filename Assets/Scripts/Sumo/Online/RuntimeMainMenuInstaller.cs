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

            Button settingsButton = CreateButton("SettingsButton", mainPanel.transform, "Settings", font);
            SetButtonHeight(settingsButton, 58f);

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

            GameObject settingsPanel = CreatePanel("SettingsPanel", overlay.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Color(0.08f, 0.1f, 0.15f, 0.95f));
            SetSize(settingsPanel, new Vector2(680f, 420f));
            AddVerticalLayout(settingsPanel, 14, new RectOffset(36, 36, 36, 36), TextAnchor.UpperCenter);

            CreateLabel("SettingsTitle", settingsPanel.transform, "Settings", font, 44, TextAnchor.MiddleCenter, Color.white);
            Text displayModeStatusText = CreateLabel("DisplayModeStatusText", settingsPanel.transform, "Current Mode: Unknown", font, 28, TextAnchor.MiddleCenter, new Color(0.84f, 0.91f, 1f, 1f));

            Button toggleDisplayModeButton = CreateButton("ToggleDisplayModeButton", settingsPanel.transform, "Switch Mode", font);
            SetButtonHeight(toggleDisplayModeButton, 60f);

            Button settingsBackButton = CreateButton("SettingsBackButton", settingsPanel.transform, "Back", font);
            SetButtonHeight(settingsBackButton, 56f);

            GameObject inGamePanel = CreatePanel("InGamePanel", overlay.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Color(0.08f, 0.1f, 0.15f, 0.95f));
            SetSize(inGamePanel, new Vector2(560f, 360f));
            AddVerticalLayout(inGamePanel, 16, new RectOffset(36, 36, 36, 36), TextAnchor.MiddleCenter);

            CreateLabel("InGameTitle", inGamePanel.transform, "Пауза", font, 46, TextAnchor.MiddleCenter, Color.white);

            Button resumeGameButton = CreateButton("ResumeGameButton", inGamePanel.transform, "Вернуться в игру", font);
            SetButtonHeight(resumeGameButton, 62f);

            Button inGameSettingsButton = CreateButton("InGameSettingsButton", inGamePanel.transform, "Настройки", font);
            SetButtonHeight(inGameSettingsButton, 58f);

            Button mainMenuButton = CreateButton("MainMenuButton", inGamePanel.transform, "Главное меню", font);
            SetButtonHeight(mainMenuButton, 58f);

            MainMenuController mainMenuController = gameObject.AddComponent<MainMenuController>();
            ClientFusionConnector connector = gameObject.AddComponent<ClientFusionConnector>();
            MatchmakingClient matchmakingClient = gameObject.AddComponent<MatchmakingClient>();
            MultiplayerMenuController multiplayerMenuController = gameObject.AddComponent<MultiplayerMenuController>();
            SettingsMenuController settingsMenuController = gameObject.AddComponent<SettingsMenuController>();
            InGameMenuController inGameMenuController = gameObject.AddComponent<InGameMenuController>();

            BootstrapConfig bootstrapConfig = Resources.Load<BootstrapConfig>("BootstrapConfig");

            matchmakingClient.Configure(bootstrapConfig, connector, false);
            mainMenuController.Configure(mainPanel, multiplayerPanel, settingsPanel, inGamePanel);
            multiplayerMenuController.Configure(
                matchmakingClient,
                mainMenuController,
                findButton,
                cancelButton,
                backButton,
                statusText,
                canvas);
            settingsMenuController.Configure(
                mainMenuController,
                toggleDisplayModeButton,
                settingsBackButton,
                displayModeStatusText);
            inGameMenuController.Configure(
                mainMenuController,
                matchmakingClient,
                connector,
                canvas,
                resumeGameButton,
                inGameSettingsButton,
                mainMenuButton);

            multiplayerButton.onClick.AddListener(mainMenuController.OpenMultiplayer);
            settingsButton.onClick.AddListener(mainMenuController.OpenSettings);
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

    [DisallowMultipleComponent]
    public sealed class InGameMenuController : MonoBehaviour
    {
        [SerializeField] private MainMenuController mainMenuController;
        [SerializeField] private MatchmakingClient matchmakingClient;
        [SerializeField] private ClientFusionConnector fusionConnector;
        [SerializeField] private Canvas menuCanvas;
        [SerializeField] private Button resumeButton;
        [SerializeField] private Button settingsButton;
        [SerializeField] private Button mainMenuButton;

        private bool _listenersBound;
        private bool _stateSubscribed;
        private bool _isConnected;
        private bool _returningToMainMenu;

        public void Configure(
            MainMenuController mainMenuControllerReference,
            MatchmakingClient matchmakingClientReference,
            ClientFusionConnector fusionConnectorReference,
            Canvas menuCanvasReference,
            Button resumeButtonReference,
            Button settingsButtonReference,
            Button mainMenuButtonReference)
        {
            UnbindButtonListeners();

            mainMenuController = mainMenuControllerReference;
            matchmakingClient = matchmakingClientReference;
            fusionConnector = fusionConnectorReference;
            menuCanvas = menuCanvasReference;
            resumeButton = resumeButtonReference;
            settingsButton = settingsButtonReference;
            mainMenuButton = mainMenuButtonReference;

            BindButtonListeners();

            if (isActiveAndEnabled)
            {
                SubscribeState();
                if (matchmakingClient != null)
                {
                    OnMatchmakingStateChanged(matchmakingClient.CurrentState, matchmakingClient.CurrentStateText);
                }
            }
        }

        private void OnEnable()
        {
            SubscribeState();
        }

        private void OnDisable()
        {
            UnsubscribeState();
        }

        private void OnDestroy()
        {
            UnbindButtonListeners();
        }

        private void Update()
        {
            if (!_isConnected || _returningToMainMenu)
            {
                return;
            }

            if (!Sumo.SumoNpcBallDriver.WasKeyPressedThisFrame(KeyCode.Escape))
            {
                return;
            }

            if (mainMenuController != null
                && (mainMenuController.IsInGameMenuOpen || mainMenuController.IsSettingsOpenFromGame))
            {
                ResumeGame();
            }
            else
            {
                OpenInGameMenu();
            }
        }

        private void OnResumePressed()
        {
            ResumeGame();
        }

        private void OnSettingsPressed()
        {
            if (!_isConnected || mainMenuController == null)
            {
                return;
            }

            if (menuCanvas != null)
            {
                menuCanvas.enabled = true;
            }

            SetGameplayCursorLock(false);
            mainMenuController.OpenSettingsFromInGame();
        }

        private async void OnMainMenuPressed()
        {
            if (_returningToMainMenu)
            {
                return;
            }

            _returningToMainMenu = true;
            SetGameplayCursorLock(false);

            if (menuCanvas != null)
            {
                menuCanvas.enabled = true;
            }

            if (mainMenuController != null)
            {
                mainMenuController.BackToMainMenu();
            }

            try
            {
                if (matchmakingClient != null)
                {
                    await matchmakingClient.ReturnToMainMenuAsync();
                }
                else if (fusionConnector != null)
                {
                    await fusionConnector.DisconnectAsync();
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                _returningToMainMenu = false;
                _isConnected = false;

                if (menuCanvas != null)
                {
                    menuCanvas.enabled = true;
                }

                if (mainMenuController != null)
                {
                    mainMenuController.BackToMainMenu();
                }
            }
        }

        private void OpenInGameMenu()
        {
            if (!_isConnected)
            {
                return;
            }

            if (menuCanvas != null)
            {
                menuCanvas.enabled = true;
            }

            SetGameplayCursorLock(false);

            if (mainMenuController != null)
            {
                mainMenuController.OpenInGameMenu();
            }
        }

        private void ResumeGame()
        {
            if (!_isConnected)
            {
                return;
            }

            if (mainMenuController != null)
            {
                mainMenuController.HideAllPanels();
            }

            if (menuCanvas != null)
            {
                menuCanvas.enabled = false;
            }

            SetGameplayCursorLock(true);
        }

        private void HideConnectedMenu()
        {
            if (mainMenuController != null)
            {
                mainMenuController.HideAllPanels();
            }

            if (menuCanvas != null)
            {
                menuCanvas.enabled = false;
            }
        }

        private void OnMatchmakingStateChanged(MatchmakingClientState state, string status)
        {
            _isConnected = state == MatchmakingClientState.Connected;

            if (_isConnected)
            {
                if (!_returningToMainMenu)
                {
                    HideConnectedMenu();
                }

                return;
            }

            SetGameplayCursorLock(false);

            if (menuCanvas != null)
            {
                menuCanvas.enabled = true;
            }

            if (mainMenuController != null
                && (mainMenuController.IsInGameMenuOpen || mainMenuController.IsSettingsOpenFromGame))
            {
                mainMenuController.BackToMainMenu();
            }
        }

        private void BindButtonListeners()
        {
            if (_listenersBound)
            {
                return;
            }

            if (resumeButton != null)
            {
                resumeButton.onClick.AddListener(OnResumePressed);
            }

            if (settingsButton != null)
            {
                settingsButton.onClick.AddListener(OnSettingsPressed);
            }

            if (mainMenuButton != null)
            {
                mainMenuButton.onClick.AddListener(OnMainMenuPressed);
            }

            _listenersBound = true;
        }

        private void UnbindButtonListeners()
        {
            if (!_listenersBound)
            {
                return;
            }

            if (resumeButton != null)
            {
                resumeButton.onClick.RemoveListener(OnResumePressed);
            }

            if (settingsButton != null)
            {
                settingsButton.onClick.RemoveListener(OnSettingsPressed);
            }

            if (mainMenuButton != null)
            {
                mainMenuButton.onClick.RemoveListener(OnMainMenuPressed);
            }

            _listenersBound = false;
        }

        private void SubscribeState()
        {
            if (_stateSubscribed || matchmakingClient == null)
            {
                return;
            }

            matchmakingClient.StateChanged += OnMatchmakingStateChanged;
            _stateSubscribed = true;
            OnMatchmakingStateChanged(matchmakingClient.CurrentState, matchmakingClient.CurrentStateText);
        }

        private void UnsubscribeState()
        {
            if (!_stateSubscribed)
            {
                return;
            }

            if (matchmakingClient != null)
            {
                matchmakingClient.StateChanged -= OnMatchmakingStateChanged;
            }

            _stateSubscribed = false;
        }

        private static void SetGameplayCursorLock(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }

    [DisallowMultipleComponent]
    public sealed class SettingsMenuController : MonoBehaviour
    {
        [SerializeField] private MainMenuController mainMenuController;
        [SerializeField] private Button toggleDisplayModeButton;
        [SerializeField] private Button backButton;
        [SerializeField] private Text displayModeStatusText;

        private const int PreferredWindowedWidth = 1280;
        private const int PreferredWindowedHeight = 720;

        private bool _listenersBound;

        public void Configure(
            MainMenuController mainMenuControllerReference,
            Button toggleDisplayModeButtonReference,
            Button backButtonReference,
            Text displayModeStatusTextReference)
        {
            UnbindButtonListeners();

            mainMenuController = mainMenuControllerReference;
            toggleDisplayModeButton = toggleDisplayModeButtonReference;
            backButton = backButtonReference;
            displayModeStatusText = displayModeStatusTextReference;

            BindButtonListeners();
            RefreshDisplayModeUi();
        }

        private void Awake()
        {
            BindButtonListeners();
        }

        private void OnEnable()
        {
            RefreshDisplayModeUi();
        }

        private void OnDestroy()
        {
            UnbindButtonListeners();
        }

        private void OnToggleDisplayModePressed()
        {
            bool isWindowed = Screen.fullScreenMode == FullScreenMode.Windowed || !Screen.fullScreen;
            ApplyDisplayMode(useFullscreen: isWindowed);
        }

        private void OnBackPressed()
        {
            if (mainMenuController != null)
            {
                mainMenuController.BackFromSettings();
            }
        }

        private void ApplyDisplayMode(bool useFullscreen)
        {
            ResetRenderScale();

            if (useFullscreen)
            {
                ResolveNativeResolution(out int width, out int height);
                Screen.SetResolution(width, height, FullScreenMode.FullScreenWindow);
            }
            else
            {
                ResolveWindowedResolution(out int width, out int height);
                Screen.SetResolution(width, height, FullScreenMode.Windowed);
            }

            RefreshDisplayModeUi();
        }

        private void RefreshDisplayModeUi()
        {
            bool isWindowed = Screen.fullScreenMode == FullScreenMode.Windowed || !Screen.fullScreen;
            string currentModeLabel = isWindowed ? "Windowed" : "Fullscreen";

            if (displayModeStatusText != null)
            {
                displayModeStatusText.text = $"Current Mode: {currentModeLabel} {Screen.width}x{Screen.height}";
            }

            SetButtonCaption(toggleDisplayModeButton, isWindowed ? "Switch To Fullscreen" : "Switch To Windowed");
        }

        private static void ResolveNativeResolution(out int width, out int height)
        {
            Resolution current = Screen.currentResolution;
            width = current.width;
            height = current.height;

            if (width <= 0 || height <= 0)
            {
                width = Display.main != null ? Display.main.systemWidth : 1920;
                height = Display.main != null ? Display.main.systemHeight : 1080;
            }

            width = Mathf.Max(640, width);
            height = Mathf.Max(360, height);
        }

        private static void ResolveWindowedResolution(out int width, out int height)
        {
            ResolveNativeResolution(out int nativeWidth, out int nativeHeight);

            int maxWidth = Mathf.Max(640, Mathf.RoundToInt(nativeWidth * 0.8f));
            int maxHeight = Mathf.Max(360, Mathf.RoundToInt(nativeHeight * 0.8f));

            width = Mathf.Min(PreferredWindowedWidth, maxWidth);
            height = Mathf.Min(PreferredWindowedHeight, maxHeight);

            float targetAspect = PreferredWindowedWidth / (float)PreferredWindowedHeight;
            if (width / (float)Mathf.Max(1, height) > targetAspect)
            {
                width = Mathf.RoundToInt(height * targetAspect);
            }
            else
            {
                height = Mathf.RoundToInt(width / targetAspect);
            }

            width = Mathf.Max(640, width);
            height = Mathf.Max(360, height);
        }

        private static void ResetRenderScale()
        {
            ScalableBufferManager.ResizeBuffers(1f, 1f);
        }

        private void BindButtonListeners()
        {
            if (_listenersBound)
            {
                return;
            }

            if (toggleDisplayModeButton != null)
            {
                toggleDisplayModeButton.onClick.AddListener(OnToggleDisplayModePressed);
            }

            if (backButton != null)
            {
                backButton.onClick.AddListener(OnBackPressed);
            }

            _listenersBound = true;
        }

        private void UnbindButtonListeners()
        {
            if (!_listenersBound)
            {
                return;
            }

            if (toggleDisplayModeButton != null)
            {
                toggleDisplayModeButton.onClick.RemoveListener(OnToggleDisplayModePressed);
            }

            if (backButton != null)
            {
                backButton.onClick.RemoveListener(OnBackPressed);
            }

            _listenersBound = false;
        }

        private static void SetButtonCaption(Button button, string caption)
        {
            if (button == null)
            {
                return;
            }

            Text label = button.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.text = caption;
            }
        }
    }
}

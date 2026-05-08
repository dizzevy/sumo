using Fusion;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Sumo.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class PlayerRoundState : NetworkBehaviour
    {
        [Header("Gameplay Components")]
        [SerializeField] private Sumo.SumoBallController ballController;
        [SerializeField] private Sumo.SumoCollisionController collisionController;
        [SerializeField] private Sumo.SumoPlayerInput playerInput;
        [SerializeField] private Sumo.SumoCameraFollow cameraFollow;
        [SerializeField] private Rigidbody body;
        [SerializeField] private Collider[] gameplayColliders;
        [SerializeField] private Renderer[] gameplayRenderers;

        [Header("Death VFX")]
        [SerializeField] private bool enableDeathVfx = true;
        [SerializeField] private int deathVfxShardCount = 24;
        [SerializeField] private float deathVfxLifetime = 2.2f;
        [SerializeField] private float deathVfxSpawnYOffset = 0.1f;
        [SerializeField] private Color deathVfxPrimaryColor = new Color(1f, 0.18f, 0.08f, 1f);
        [SerializeField] private Color deathVfxHighlightColor = new Color(1f, 0.62f, 0.12f, 1f);
        [SerializeField] private float deathVfxIntensity = 1.55f;
        [SerializeField] private float deathToSpectatorDelay = -1f;

        [Networked] public NetworkBool IsClientReady { get; private set; }
        [Networked] public NetworkBool IsAlive { get; private set; }
        [Networked] public NetworkBool IsSpectator { get; private set; }
        [Networked] public int LastRoundIndex { get; private set; }
        [Networked] public int EliminationOrder { get; private set; }
        [Networked] public int ParticipantRawEncoded { get; private set; }
        [Networked] public int SelectedClassRaw { get; private set; }
        [Networked] private NetworkBool PendingSpectatorTransition { get; set; }
        [Networked] private TickTimer PendingSpectatorTransitionTimer { get; set; }
        [Networked] private Vector3 PendingSpectatorPosition { get; set; }
        [Networked] private Vector3 PendingSpectatorEulerAngles { get; set; }

        public bool IsAliveInRound => IsAlive && !IsSpectator;
        public Sumo.SumoPlayerClass SelectedClass => Sumo.SumoPlayerClassCatalog.FromRaw(SelectedClassRaw);

        public Vector3 ZoneCheckPosition
        {
            get
            {
                if (body != null)
                {
                    return body.worldCenterOfMass;
                }

                return transform.position;
            }
        }

        private bool _stateApplied;
        private NetworkBool _lastAppliedAlive;
        private NetworkBool _lastAppliedSpectator;
        private bool[] _gameplayRendererDefaultEnabled;

        private void Awake()
        {
            CacheComponentsIfNeeded();
        }

        public override void Spawned()
        {
            CacheComponentsIfNeeded();

            if (HasStateAuthority)
            {
                IsAlive = false;
                IsSpectator = true;
                EliminationOrder = 0;
                PendingSpectatorTransition = false;
                PendingSpectatorTransitionTimer = default;
                PendingSpectatorPosition = Vector3.zero;
                PendingSpectatorEulerAngles = Vector3.zero;
                LastRoundIndex = 0;
                IsClientReady = false;
                ParticipantRawEncoded = PlayerRef.None.RawEncoded;
                SelectedClassRaw = (int)Sumo.SumoPlayerClass.None;
            }

            if (Object != null && Object.HasInputAuthority)
            {
                EnsureLocalHudController();
                EnsureClassSelectionUiController();
            }

            EnsureNameLabelController();
            ApplyState(force: true);
        }

        public override void Render()
        {
            ApplyState(force: false);
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority || !PendingSpectatorTransition)
            {
                return;
            }

            if (!PendingSpectatorTransitionTimer.ExpiredOrNotRunning(Runner))
            {
                return;
            }

            FinalizePendingSpectatorTransition();
        }

        public void ServerPrepareForRound(int roundIndex, Vector3 position, Quaternion rotation, int participantRawEncoded = 0)
        {
            if (!HasStateAuthority)
            {
                return;
            }

            LastRoundIndex = Mathf.Max(0, roundIndex);
            EliminationOrder = 0;
            IsAlive = true;
            IsSpectator = false;
            if (SelectedClassRaw == (int)Sumo.SumoPlayerClass.None)
            {
                SelectedClassRaw = (int)Sumo.SumoPlayerClassCatalog.DefaultClass;
            }

            ParticipantRawEncoded = participantRawEncoded;
            PendingSpectatorTransition = false;
            PendingSpectatorTransitionTimer = default;
            PendingSpectatorPosition = Vector3.zero;
            PendingSpectatorEulerAngles = Vector3.zero;
            if (ballController != null)
            {
                ballController.ServerResetClassAbilityState();
            }

            TeleportServer(position, rotation);
            ApplyState(force: true);
        }

        public void ServerEliminateToSpectator(int eliminationOrder, Vector3 spectatorPosition, Quaternion spectatorRotation)
        {
            if (!HasStateAuthority)
            {
                return;
            }

            if (enableDeathVfx)
            {
                Vector3 deathPosition = ZoneCheckPosition + Vector3.up * deathVfxSpawnYOffset;
                Color tint = ResolveDeathTint();
                Color highlightTint = ResolveDeathHighlightTint();
                RPC_PlayDeathVfx(
                    deathPosition,
                    new Vector3(tint.r, tint.g, tint.b),
                    new Vector3(highlightTint.r, highlightTint.g, highlightTint.b),
                    deathVfxShardCount,
                    deathVfxLifetime,
                    deathVfxIntensity);
            }

            EliminationOrder = Mathf.Max(1, eliminationOrder);
            IsAlive = false;
            float transitionDelaySeconds = ResolveDeathToSpectatorDelay();
            if (transitionDelaySeconds <= 0.001f)
            {
                IsSpectator = true;
                PendingSpectatorTransition = false;
                PendingSpectatorTransitionTimer = default;
                PendingSpectatorPosition = Vector3.zero;
                PendingSpectatorEulerAngles = Vector3.zero;
                TeleportServer(spectatorPosition, spectatorRotation);
            }
            else
            {
                IsSpectator = false;
                PendingSpectatorTransition = true;
                PendingSpectatorTransitionTimer = TickTimer.CreateFromSeconds(Runner, transitionDelaySeconds);
                PendingSpectatorPosition = spectatorPosition;
                PendingSpectatorEulerAngles = spectatorRotation.eulerAngles;
            }

            ApplyState(force: true);
        }

        public void ServerForceClientReady(bool value)
        {
            if (!HasStateAuthority)
            {
                return;
            }

            IsClientReady = value;
        }

        public void ServerBeginClassSelection()
        {
            if (!HasStateAuthority)
            {
                return;
            }

            if (SelectedClassRaw == (int)Sumo.SumoPlayerClass.None)
            {
                SelectedClassRaw = (int)Sumo.SumoPlayerClassCatalog.DefaultClass;
            }

            IsClientReady = false;
        }

        public void ServerConfirmClassSelection(Sumo.SumoPlayerClass playerClass)
        {
            if (!HasStateAuthority)
            {
                return;
            }

            SelectedClassRaw = (int)Sumo.SumoPlayerClassCatalog.Sanitize(playerClass);
            IsClientReady = true;
        }

        public void ServerPreviewClassSelection(Sumo.SumoPlayerClass playerClass)
        {
            if (!HasStateAuthority || IsClientReady)
            {
                return;
            }

            SelectedClassRaw = (int)Sumo.SumoPlayerClassCatalog.Sanitize(playerClass);
        }

        public void ServerAutoConfirmClassSelection()
        {
            if (!HasStateAuthority)
            {
                return;
            }

            ServerConfirmClassSelection(Sumo.SumoPlayerClassCatalog.FromRaw(SelectedClassRaw));
        }

        public void SubmitClassSelection(Sumo.SumoPlayerClass playerClass)
        {
            Sumo.SumoPlayerClass sanitizedClass = Sumo.SumoPlayerClassCatalog.Sanitize(playerClass);

            if (HasStateAuthority)
            {
                ServerConfirmClassSelection(sanitizedClass);
                return;
            }

            if (Object == null || !Object.HasInputAuthority)
            {
                return;
            }

            RPC_SubmitClassSelection((int)sanitizedClass);
        }

        public void PreviewClassSelection(Sumo.SumoPlayerClass playerClass)
        {
            Sumo.SumoPlayerClass sanitizedClass = Sumo.SumoPlayerClassCatalog.Sanitize(playerClass);

            if (HasStateAuthority)
            {
                ServerPreviewClassSelection(sanitizedClass);
                return;
            }

            if (Object == null || !Object.HasInputAuthority)
            {
                return;
            }

            RPC_PreviewClassSelection((int)sanitizedClass);
        }

        [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority, Channel = RpcChannel.Reliable)]
        private void RPC_ReportClientReady()
        {
            IsClientReady = true;
        }

        [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority, Channel = RpcChannel.Reliable)]
        private void RPC_SubmitClassSelection(int rawClass)
        {
            ServerConfirmClassSelection(Sumo.SumoPlayerClassCatalog.FromRaw(rawClass));
        }

        [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority, Channel = RpcChannel.Reliable)]
        private void RPC_PreviewClassSelection(int rawClass)
        {
            ServerPreviewClassSelection(Sumo.SumoPlayerClassCatalog.FromRaw(rawClass));
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All, Channel = RpcChannel.Reliable)]
        private void RPC_PlayDeathVfx(Vector3 position, Vector3 tintRgb, Vector3 highlightRgb, int shardCount, float lifetime, float intensity)
        {
            if (Application.isBatchMode && Runner != null && Runner.IsServer && !Runner.IsClient)
            {
                return;
            }

            Color tint = new Color(
                Mathf.Clamp01(tintRgb.x),
                Mathf.Clamp01(tintRgb.y),
                Mathf.Clamp01(tintRgb.z),
                1f);

            Color highlightTint = new Color(
                Mathf.Clamp01(highlightRgb.x),
                Mathf.Clamp01(highlightRgb.y),
                Mathf.Clamp01(highlightRgb.z),
                1f);

            PlayerDeathVfx.Spawn(position, tint, highlightTint, shardCount, lifetime, intensity);
        }

        private void TeleportServer(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);

            if (body == null)
            {
                return;
            }

            body.position = position;
            body.rotation = rotation;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        private void ApplyState(bool force)
        {
            if (!force && _stateApplied && _lastAppliedAlive == IsAlive && _lastAppliedSpectator == IsSpectator)
            {
                return;
            }

            CacheComponentsIfNeeded();

            bool gameplayEnabled = IsAlive && !IsSpectator;
            bool keepLocalDeathCamera = !IsAlive && !IsSpectator && PendingSpectatorTransition;
            bool localPlayer = Object != null && Object.HasInputAuthority;

            if (ballController != null)
            {
                ballController.enabled = gameplayEnabled;
            }

            if (collisionController != null)
            {
                collisionController.enabled = gameplayEnabled;
            }

            if (playerInput != null)
            {
                if (localPlayer)
                {
                    playerInput.enabled = gameplayEnabled;
                }
                else
                {
                    playerInput.enabled = false;
                }
            }

            if (cameraFollow != null && localPlayer)
            {
                cameraFollow.enabled = gameplayEnabled || keepLocalDeathCamera;
            }

            if (gameplayColliders != null)
            {
                for (int i = 0; i < gameplayColliders.Length; i++)
                {
                    Collider col = gameplayColliders[i];
                    if (col == null || col.isTrigger)
                    {
                        continue;
                    }

                    col.enabled = gameplayEnabled;
                }
            }

            bool showGameplayVisuals = IsAlive;
            if (gameplayRenderers != null)
            {
                for (int i = 0; i < gameplayRenderers.Length; i++)
                {
                    Renderer renderer = gameplayRenderers[i];
                    if (renderer == null)
                    {
                        continue;
                    }

                    bool defaultEnabled = _gameplayRendererDefaultEnabled != null
                                          && i < _gameplayRendererDefaultEnabled.Length
                                          && _gameplayRendererDefaultEnabled[i];
                    renderer.enabled = showGameplayVisuals && defaultEnabled;
                }
            }

            if (body != null)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = !gameplayEnabled;
                body.detectCollisions = gameplayEnabled;
            }

            _lastAppliedAlive = IsAlive;
            _lastAppliedSpectator = IsSpectator;
            _stateApplied = true;
        }

        private void FinalizePendingSpectatorTransition()
        {
            if (!HasStateAuthority || !PendingSpectatorTransition)
            {
                return;
            }

            Vector3 spectatorPosition = PendingSpectatorPosition;
            Quaternion spectatorRotation = Quaternion.Euler(PendingSpectatorEulerAngles);

            PendingSpectatorTransition = false;
            PendingSpectatorTransitionTimer = default;
            PendingSpectatorPosition = Vector3.zero;
            PendingSpectatorEulerAngles = Vector3.zero;

            IsSpectator = true;
            TeleportServer(spectatorPosition, spectatorRotation);
            ApplyState(force: true);
        }

        private void CacheComponentsIfNeeded()
        {
            if (ballController == null)
            {
                ballController = GetComponent<Sumo.SumoBallController>();
            }

            if (collisionController == null)
            {
                collisionController = GetComponent<Sumo.SumoCollisionController>();
            }

            if (playerInput == null)
            {
                playerInput = GetComponent<Sumo.SumoPlayerInput>();
            }

            if (cameraFollow == null)
            {
                cameraFollow = GetComponent<Sumo.SumoCameraFollow>();
            }

            if (body == null)
            {
                body = GetComponent<Rigidbody>();
            }

            if (gameplayColliders == null || gameplayColliders.Length == 0)
            {
                gameplayColliders = GetComponentsInChildren<Collider>(includeInactive: true);
            }

            if (gameplayRenderers == null || gameplayRenderers.Length == 0)
            {
                gameplayRenderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            }

            if (gameplayRenderers != null && gameplayRenderers.Length > 0)
            {
                if (_gameplayRendererDefaultEnabled == null || _gameplayRendererDefaultEnabled.Length != gameplayRenderers.Length)
                {
                    _gameplayRendererDefaultEnabled = new bool[gameplayRenderers.Length];
                    for (int i = 0; i < gameplayRenderers.Length; i++)
                    {
                        Renderer renderer = gameplayRenderers[i];
                        _gameplayRendererDefaultEnabled[i] = renderer != null && renderer.enabled;
                    }
                }
            }
        }

        private void OnValidate()
        {
            CacheComponentsIfNeeded();
            deathVfxShardCount = Mathf.Clamp(deathVfxShardCount, 6, 96);
            deathVfxLifetime = Mathf.Max(0.35f, deathVfxLifetime);
            deathVfxSpawnYOffset = Mathf.Clamp(deathVfxSpawnYOffset, -1f, 1f);
            deathVfxPrimaryColor = new Color(
                Mathf.Clamp01(deathVfxPrimaryColor.r),
                Mathf.Clamp01(deathVfxPrimaryColor.g),
                Mathf.Clamp01(deathVfxPrimaryColor.b),
                1f);
            deathVfxHighlightColor = new Color(
                Mathf.Clamp01(deathVfxHighlightColor.r),
                Mathf.Clamp01(deathVfxHighlightColor.g),
                Mathf.Clamp01(deathVfxHighlightColor.b),
                1f);
            deathVfxIntensity = Mathf.Clamp(deathVfxIntensity, 1f, 3f);
            if (deathToSpectatorDelay < 0f)
            {
                deathToSpectatorDelay = -1f;
            }
            else
            {
                deathToSpectatorDelay = Mathf.Max(0f, deathToSpectatorDelay);
            }
        }

        private void EnsureLocalHudController()
        {
            MatchHudController existing = GetComponent<MatchHudController>();
            if (existing == null)
            {
                gameObject.AddComponent<MatchHudController>();
            }
        }

        private void EnsureClassSelectionUiController()
        {
            ClassSelectionUiController existing = GetComponent<ClassSelectionUiController>();
            if (existing == null)
            {
                gameObject.AddComponent<ClassSelectionUiController>();
            }
        }

        private void EnsureNameLabelController()
        {
            PlayerNameLabelController existing = GetComponent<PlayerNameLabelController>();
            if (existing == null)
            {
                gameObject.AddComponent<PlayerNameLabelController>();
            }
        }

        private Color ResolveDeathTint()
        {
            return deathVfxPrimaryColor;
        }

        private Color ResolveDeathHighlightTint()
        {
            return deathVfxHighlightColor;
        }

        private float ResolveDeathToSpectatorDelay()
        {
            if (deathToSpectatorDelay >= 0f)
            {
                return Mathf.Max(0f, deathToSpectatorDelay);
            }

            return enableDeathVfx ? Mathf.Max(0f, deathVfxLifetime) : 0f;
        }
    }

    [DisallowMultipleComponent]
    internal sealed class ClassSelectionUiController : MonoBehaviour
    {
        [SerializeField] private NetworkObject networkObject;
        [SerializeField] private PlayerRoundState playerRoundState;
        [SerializeField] private MatchRoundManager matchRoundManager;

        private Canvas _runtimeCanvas;
        private GameObject _rootPanel;
        private Image _previewBall;
        private Text _timerText;
        private Text _classNameText;
        private Text _abilityNameText;
        private Text _descriptionText;
        private Text _statsText;
        private Text _statusText;
        private Button _leftButton;
        private Button _rightButton;
        private Button _confirmButton;
        private int _selectedIndex;
        private bool _selectionInitialized;
        private bool _uiReady;

        private static Sprite _circleSprite;

        private void Awake()
        {
            CacheReferencesIfNeeded();
        }

        private void OnDestroy()
        {
            if (_runtimeCanvas != null)
            {
                Destroy(_runtimeCanvas.gameObject);
            }
        }

        private void Update()
        {
            if (!IsLocalPlayer())
            {
                SetVisible(false);
                _selectionInitialized = false;
                return;
            }

            CacheReferencesIfNeeded();
            ResolveManagerIfNeeded();

            bool shouldShow = matchRoundManager != null
                              && matchRoundManager.TryGetState(out MatchState state)
                              && state == MatchState.ClassSelection;
            if (!shouldShow)
            {
                SetVisible(false);
                _selectionInitialized = false;
                return;
            }

            EnsureUi();
            if (!_uiReady)
            {
                return;
            }

            SetVisible(true);
            InitializeSelectionIfNeeded();
            HandleKeyboardInput();
            RefreshStateTexts();
        }

        private void InitializeSelectionIfNeeded()
        {
            if (_selectionInitialized)
            {
                return;
            }

            Sumo.SumoPlayerClass selectedClass = playerRoundState != null
                ? playerRoundState.SelectedClass
                : Sumo.SumoPlayerClassCatalog.DefaultClass;
            _selectedIndex = Sumo.SumoPlayerClassCatalog.GetIndex(selectedClass);
            _selectionInitialized = true;
            RefreshClassUi();
            PublishSelectionPreview();
        }

        private void HandleKeyboardInput()
        {
            if (playerRoundState == null || playerRoundState.IsClientReady)
            {
                return;
            }

            if (Sumo.SumoNpcBallDriver.WasKeyPressedThisFrame(KeyCode.LeftArrow)
                || Sumo.SumoNpcBallDriver.WasKeyPressedThisFrame(KeyCode.A))
            {
                CycleSelection(-1);
            }
            else if (Sumo.SumoNpcBallDriver.WasKeyPressedThisFrame(KeyCode.RightArrow)
                     || Sumo.SumoNpcBallDriver.WasKeyPressedThisFrame(KeyCode.D))
            {
                CycleSelection(1);
            }

            if (Sumo.SumoNpcBallDriver.WasKeyPressedThisFrame(KeyCode.Return)
                || Sumo.SumoNpcBallDriver.WasKeyPressedThisFrame(KeyCode.KeypadEnter))
            {
                ConfirmSelection();
            }
        }

        private void CycleSelection(int direction)
        {
            if (playerRoundState != null && playerRoundState.IsClientReady)
            {
                return;
            }

            _selectedIndex += direction;
            _selectedIndex = ((_selectedIndex % Sumo.SumoPlayerClassCatalog.Count) + Sumo.SumoPlayerClassCatalog.Count) % Sumo.SumoPlayerClassCatalog.Count;
            RefreshClassUi();
            PublishSelectionPreview();
        }

        private void ConfirmSelection()
        {
            if (playerRoundState == null || playerRoundState.IsClientReady)
            {
                return;
            }

            playerRoundState.SubmitClassSelection(Sumo.SumoPlayerClassCatalog.GetByIndex(_selectedIndex));
            RefreshStateTexts();
        }

        private void RefreshClassUi()
        {
            Sumo.SumoPlayerClassDefinition definition = Sumo.SumoPlayerClassCatalog.GetDefinition(
                Sumo.SumoPlayerClassCatalog.GetByIndex(_selectedIndex));

            if (_previewBall != null)
            {
                _previewBall.color = definition.Color;
            }

            if (_classNameText != null)
            {
                _classNameText.text = definition.DisplayName;
            }

            if (_abilityNameText != null)
            {
                _abilityNameText.text = $"Ability: {definition.AbilityName}";
            }

            if (_descriptionText != null)
            {
                _descriptionText.text = definition.Description;
            }

            if (_statsText != null)
            {
                _statsText.text = BuildStatsTable(definition);
            }
        }

        private void PublishSelectionPreview()
        {
            if (playerRoundState == null || playerRoundState.IsClientReady)
            {
                return;
            }

            playerRoundState.PreviewClassSelection(Sumo.SumoPlayerClassCatalog.GetByIndex(_selectedIndex));
        }

        private void RefreshStateTexts()
        {
            if (matchRoundManager != null
                && matchRoundManager.TryGetState(out MatchState state)
                && state == MatchState.ClassSelection
                && _timerText != null)
            {
                int seconds = Mathf.Max(0, Mathf.CeilToInt(matchRoundManager.RemainingPhaseTime));
                _timerText.text = $"Class selection: {seconds}s";
            }

            bool ready = playerRoundState != null && playerRoundState.IsClientReady;
            if (_statusText != null)
            {
                _statusText.text = ready ? "Locked. Waiting for players..." : "Pick a class and lock it in.";
            }

            if (_leftButton != null)
            {
                _leftButton.interactable = !ready;
            }

            if (_rightButton != null)
            {
                _rightButton.interactable = !ready;
            }

            if (_confirmButton != null)
            {
                _confirmButton.interactable = !ready;
            }
        }

        private bool IsLocalPlayer()
        {
            return networkObject != null
                   && networkObject.Runner != null
                   && networkObject.Runner.IsRunning
                   && networkObject.HasInputAuthority;
        }

        private void EnsureUi()
        {
            if (_uiReady)
            {
                return;
            }

            CreateRuntimeCanvas();
            _uiReady = _runtimeCanvas != null && _rootPanel != null;
        }

        private void CreateRuntimeCanvas()
        {
            if (_runtimeCanvas != null)
            {
                return;
            }

            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font == null)
            {
                font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }

            GameObject canvasObject = new GameObject($"{name}_ClassSelectionCanvas");
            _runtimeCanvas = canvasObject.AddComponent<Canvas>();
            _runtimeCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _runtimeCanvas.sortingOrder = 5200;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();

            _rootPanel = CreatePanel("ClassSelectionRoot", canvasObject.transform, Vector2.zero, Vector2.one, new Color(0.055f, 0.065f, 0.07f, 1f));

            _timerText = CreateText(
                "Timer",
                _rootPanel.transform,
                font,
                34,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                new Color(1f, 0.94f, 0.68f, 1f));
            SetAnchored(_timerText.rectTransform, new Vector2(0.5f, 0.93f), new Vector2(0.5f, 0.93f), new Vector2(0f, 0f), new Vector2(820f, 58f));

            GameObject previewArea = new GameObject("PreviewArea", typeof(RectTransform));
            previewArea.transform.SetParent(_rootPanel.transform, false);
            RectTransform previewRect = previewArea.GetComponent<RectTransform>();
            SetAnchored(previewRect, new Vector2(0.06f, 0.15f), new Vector2(0.50f, 0.83f), Vector2.zero, Vector2.zero);

            Image previewShadow = new GameObject("ClassBallShadow", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            previewShadow.transform.SetParent(previewArea.transform, false);
            previewShadow.sprite = GetCircleSprite();
            previewShadow.preserveAspect = true;
            previewShadow.color = new Color(0f, 0f, 0f, 0.52f);
            SetAnchored(previewShadow.rectTransform, new Vector2(0.5f, 0.53f), new Vector2(0.5f, 0.53f), new Vector2(26f, -34f), new Vector2(430f, 430f));

            _previewBall = new GameObject("ClassBallPreview", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            _previewBall.transform.SetParent(previewArea.transform, false);
            _previewBall.sprite = GetCircleSprite();
            _previewBall.preserveAspect = true;
            SetAnchored(_previewBall.rectTransform, new Vector2(0.5f, 0.53f), new Vector2(0.5f, 0.53f), Vector2.zero, new Vector2(430f, 430f));

            Outline previewOutline = _previewBall.gameObject.AddComponent<Outline>();
            previewOutline.effectColor = new Color(0.015f, 0.012f, 0.018f, 0.92f);
            previewOutline.effectDistance = new Vector2(4f, -4f);

            _leftButton = CreateButton("PreviousClassButton", previewArea.transform, "Previous", font, 24);
            SetAnchored(_leftButton.GetComponent<RectTransform>(), new Vector2(0.36f, 0.08f), new Vector2(0.36f, 0.08f), Vector2.zero, new Vector2(190f, 58f));
            _leftButton.onClick.AddListener(() => CycleSelection(-1));

            _rightButton = CreateButton("NextClassButton", previewArea.transform, "Next", font, 24);
            SetAnchored(_rightButton.GetComponent<RectTransform>(), new Vector2(0.64f, 0.08f), new Vector2(0.64f, 0.08f), Vector2.zero, new Vector2(190f, 58f));
            _rightButton.onClick.AddListener(() => CycleSelection(1));

            GameObject statsPanel = CreatePanel("ClassStatsPanel", _rootPanel.transform, new Vector2(0.56f, 0.15f), new Vector2(0.92f, 0.83f), new Color(0.92f, 0.88f, 0.76f, 1f));
            Outline panelOutline = statsPanel.AddComponent<Outline>();
            panelOutline.effectColor = new Color(0.015f, 0.012f, 0.018f, 1f);
            panelOutline.effectDistance = new Vector2(4f, -4f);

            _classNameText = CreateText("ClassName", statsPanel.transform, font, 46, FontStyle.Bold, TextAnchor.UpperLeft, new Color(0.04f, 0.035f, 0.045f, 1f));
            SetStretchTop(_classNameText.rectTransform, 44f, 44f, 34f, 60f);

            _abilityNameText = CreateText("AbilityName", statsPanel.transform, font, 30, FontStyle.Bold, TextAnchor.UpperLeft, new Color(0.88f, 0.18f, 0.16f, 1f));
            SetStretchTop(_abilityNameText.rectTransform, 44f, 44f, 104f, 42f);

            _descriptionText = CreateText("Description", statsPanel.transform, font, 23, FontStyle.Normal, TextAnchor.UpperLeft, new Color(0.075f, 0.07f, 0.08f, 1f));
            SetStretchTop(_descriptionText.rectTransform, 44f, 44f, 160f, 76f);

            Image divider = new GameObject("ClassStatsDivider", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            divider.transform.SetParent(statsPanel.transform, false);
            divider.color = new Color(0.02f, 0.018f, 0.024f, 0.84f);
            SetStretchTop(divider.rectTransform, 44f, 44f, 254f, 2f);

            _statsText = CreateText("Stats", statsPanel.transform, font, 25, FontStyle.Normal, TextAnchor.UpperLeft, new Color(0.04f, 0.035f, 0.045f, 1f));
            SetStretchTop(_statsText.rectTransform, 44f, 44f, 282f, 270f);

            _statusText = CreateText("ReadyStatus", statsPanel.transform, font, 23, FontStyle.Bold, TextAnchor.MiddleCenter, new Color(0.04f, 0.38f, 0.68f, 1f));
            SetStretchBottom(_statusText.rectTransform, 44f, 44f, 106f, 34f);

            _confirmButton = CreateButton("ConfirmClassButton", statsPanel.transform, "Lock Class", font, 30);
            SetAnchored(_confirmButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 34f), new Vector2(310f, 64f));
            _confirmButton.onClick.AddListener(ConfirmSelection);

            SetVisible(false);
        }

        private static GameObject CreatePanel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Color color)
        {
            GameObject panel = new GameObject(name, typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(parent, false);

            RectTransform rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            Image image = panel.GetComponent<Image>();
            image.color = color;
            return panel;
        }

        private static Text CreateText(string name, Transform parent, Font font, int fontSize, FontStyle style, TextAnchor anchor, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);

            Text text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = Mathf.Max(10, fontSize);
            text.fontStyle = style;
            text.alignment = anchor;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static Button CreateButton(string name, Transform parent, string caption, Font font, int fontSize)
        {
            GameObject buttonObject = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);

            Image image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.96f, 0.76f, 0.16f, 1f);

            Button button = buttonObject.GetComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(0.96f, 0.76f, 0.16f, 1f);
            colors.highlightedColor = new Color(1f, 0.9f, 0.28f, 1f);
            colors.pressedColor = new Color(0.88f, 0.18f, 0.16f, 1f);
            colors.disabledColor = new Color(0.34f, 0.32f, 0.30f, 0.78f);
            button.colors = colors;

            Outline outline = buttonObject.AddComponent<Outline>();
            outline.effectColor = new Color(0.015f, 0.012f, 0.018f, 1f);
            outline.effectDistance = new Vector2(3f, -3f);

            Text label = CreateText("Label", buttonObject.transform, font, fontSize, FontStyle.Bold, TextAnchor.MiddleCenter, new Color(0.025f, 0.02f, 0.025f, 1f));
            RectTransform labelRect = label.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            return button;
        }

        private static string BuildStatsTable(Sumo.SumoPlayerClassDefinition definition)
        {
            switch (definition.Class)
            {
                case Sumo.SumoPlayerClass.Fatso:
                    return "Size                 x3\nSpeed                x1/3 while active\nIncoming push        x0.30; high-speed hits pierce up to x0.90\nOutgoing push        x5\nActive time          10s\nRecharge             30s";
                case Sumo.SumoPlayerClass.Jumper:
                default:
                    return "Role                 Mobility\nJump boost           +12 velocity\nAbility input        F while active\nActive time          10s\nRecharge             30s";
            }
        }

        private static void SetAnchored(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPosition, Vector2 sizeDelta)
        {
            if (rect == null)
            {
                return;
            }

            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;
        }

        private static void SetStretchTop(RectTransform rect, float left, float right, float top, float height)
        {
            if (rect == null)
            {
                return;
            }

            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2((left - right) * 0.5f, -top);
            rect.sizeDelta = new Vector2(-(left + right), height);
        }

        private static void SetStretchBottom(RectTransform rect, float left, float right, float bottom, float height)
        {
            if (rect == null)
            {
                return;
            }

            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2((left - right) * 0.5f, bottom);
            rect.sizeDelta = new Vector2(-(left + right), height);
        }

        private static Sprite GetCircleSprite()
        {
            if (_circleSprite != null)
            {
                return _circleSprite;
            }

            const int size = 128;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "ClassSelectionCircle"
            };

            Color clear = new Color(1f, 1f, 1f, 0f);
            Color white = Color.white;
            Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
            float radius = size * 0.48f;
            float softEdge = 1.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float distance = Vector2.Distance(new Vector2(x, y), center);
                    float alpha = Mathf.Clamp01((radius - distance) / softEdge);
                    texture.SetPixel(x, y, alpha > 0f ? new Color(white.r, white.g, white.b, alpha) : clear);
                }
            }

            texture.Apply(false, true);
            _circleSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            return _circleSprite;
        }

        private void SetVisible(bool visible)
        {
            if (_rootPanel != null)
            {
                _rootPanel.SetActive(visible);
            }

            if (_runtimeCanvas != null)
            {
                _runtimeCanvas.enabled = visible;
            }
        }

        private void ResolveManagerIfNeeded()
        {
            if (matchRoundManager == null)
            {
                matchRoundManager = FindObjectOfType<MatchRoundManager>(true);
            }
        }

        private void CacheReferencesIfNeeded()
        {
            if (networkObject == null)
            {
                networkObject = GetComponent<NetworkObject>();
            }

            if (playerRoundState == null)
            {
                playerRoundState = GetComponent<PlayerRoundState>();
            }
        }
    }

    internal static class PlayerDeathVfx
    {
        private static Mesh _sphereMesh;

        public static void Spawn(Vector3 position, Color tint, Color highlightTint, int shardCount, float lifetimeSeconds, float intensity)
        {
            float intensityScale = Mathf.Clamp(intensity, 1f, 3f);
            float intensity01 = (intensityScale - 1f) / 2f;
            float lifetime = Mathf.Clamp(lifetimeSeconds * Mathf.Lerp(1f, 1.3f, intensity01), 0.35f, 9f);
            int shards = Mathf.Clamp(Mathf.RoundToInt(shardCount * Mathf.Lerp(1.2f, 1.9f, intensity01)), 8, 128);

            GameObject root = new GameObject("PlayerDeathVfx");
            root.transform.position = position;

            TimedDestroy timedDestroy = root.AddComponent<TimedDestroy>();
            timedDestroy.Lifetime = lifetime;

            SpawnFlashLight(root.transform, tint, highlightTint, lifetime, intensityScale);
            SpawnBurstParticles(root.transform, tint, highlightTint, lifetime, intensityScale);
            SpawnSparkSpray(root.transform, tint, highlightTint, lifetime, intensityScale);
            SpawnShockwave(root.transform, tint, highlightTint, lifetime, intensityScale);
            SpawnShards(root.transform, tint, highlightTint, shards, lifetime, intensityScale);
        }

        private static void SpawnBurstParticles(Transform parent, Color tint, Color highlightTint, float lifetime, float intensity)
        {
            GameObject particlesObject = new GameObject("BurstParticles");
            particlesObject.transform.SetParent(parent, false);

            ParticleSystem ps = particlesObject.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.loop = false;
            main.playOnAwake = true;
            main.duration = Mathf.Min(1.35f, lifetime);
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.28f, 0.72f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(6.5f * intensity, 15.8f * intensity);
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.23f);
            main.maxParticles = 260;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(highlightTint.r, highlightTint.g, highlightTint.b, 1f),
                new Color(tint.r, tint.g, tint.b, 0.78f));

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            short firstBurstMin = (short)Mathf.RoundToInt(48f * intensity);
            short firstBurstMax = (short)Mathf.RoundToInt(76f * intensity);
            short secondBurstMin = (short)Mathf.RoundToInt(20f * intensity);
            short secondBurstMax = (short)Mathf.RoundToInt(34f * intensity);
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, firstBurstMin, firstBurstMax, 1, 0f),
                new ParticleSystem.Burst(0.07f, secondBurstMin, secondBurstMax, 1, 0f)
            });

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.26f;

            ParticleSystem.ColorOverLifetimeModule colorLifetime = ps.colorOverLifetime;
            colorLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.Lerp(highlightTint, Color.white, 0.28f), 0f),
                    new GradientColorKey(highlightTint, 0.42f),
                    new GradientColorKey(tint, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.85f, 0.35f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorLifetime.color = gradient;

            ParticleSystem.SizeOverLifetimeModule sizeLifetime = ps.sizeOverLifetime;
            sizeLifetime.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.55f, 0.68f),
                new Keyframe(1f, 0f));
            sizeLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.sharedMaterial = CreateParticleMaterial(tint);

            ps.Play(true);
        }

        private static void SpawnSparkSpray(Transform parent, Color tint, Color highlightTint, float lifetime, float intensity)
        {
            GameObject sparksObject = new GameObject("SparkSpray");
            sparksObject.transform.SetParent(parent, false);

            ParticleSystem ps = sparksObject.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.loop = false;
            main.playOnAwake = true;
            main.duration = Mathf.Min(1.6f, lifetime);
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.95f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(9f * intensity, 22f * intensity);
            main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.08f);
            main.maxParticles = 180;
            main.gravityModifier = 0.65f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(highlightTint.r, highlightTint.g, highlightTint.b, 1f),
                new Color(tint.r, tint.g, tint.b, 0.9f));

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            short burstMin = (short)Mathf.RoundToInt(34f * intensity);
            short burstMax = (short)Mathf.RoundToInt(62f * intensity);
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0.02f, burstMin, burstMax, 1, 0f)
            });

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 28f;
            shape.radius = 0.08f;
            shape.arcMode = ParticleSystemShapeMultiModeValue.Random;

            ParticleSystem.ColorOverLifetimeModule colorLifetime = ps.colorOverLifetime;
            colorLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(highlightTint, 0f),
                    new GradientColorKey(new Color(1f, 0.35f, 0.14f, 1f), 0.55f),
                    new GradientColorKey(new Color(0.65f, 0.09f, 0.04f, 1f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.72f, 0.45f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorLifetime.color = gradient;

            ParticleSystem.TrailModule trails = ps.trails;
            trails.enabled = true;
            trails.mode = ParticleSystemTrailMode.PerParticle;
            trails.lifetime = 0.16f;
            trails.ratio = 1f;
            trails.dieWithParticles = true;
            trails.minVertexDistance = 0.04f;
            trails.widthOverTrail = new ParticleSystem.MinMaxCurve(0.9f);

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.25f;
            renderer.lengthScale = 1.8f;
            renderer.sharedMaterial = CreateParticleMaterial(highlightTint);

            ps.Play(true);
        }

        private static void SpawnShockwave(Transform parent, Color tint, Color highlightTint, float lifetime, float intensity)
        {
            GameObject waveObject = new GameObject("Shockwave");
            waveObject.transform.SetParent(parent, false);

            ParticleSystem ps = waveObject.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.loop = false;
            main.playOnAwake = true;
            main.duration = Mathf.Min(0.75f, lifetime);
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.42f, 0.65f);
            main.startSpeed = 0f;
            main.startSize = 0.36f;
            main.maxParticles = 6;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(highlightTint.r, highlightTint.g, highlightTint.b, 0.72f),
                new Color(tint.r, tint.g, tint.b, 0.6f));

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, 1, 2, 1, 0f)
            });

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.02f;

            ParticleSystem.SizeOverLifetimeModule sizeLifetime = ps.sizeOverLifetime;
            sizeLifetime.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0.2f),
                new Keyframe(0.28f, 1.7f * intensity),
                new Keyframe(1f, 3.9f * intensity));
            sizeLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            ParticleSystem.ColorOverLifetimeModule colorLifetime = ps.colorOverLifetime;
            colorLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(highlightTint, 0f),
                    new GradientColorKey(tint, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0.95f, 0f),
                    new GradientAlphaKey(0.42f, 0.38f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorLifetime.color = gradient;

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = CreateParticleMaterial(highlightTint);

            ps.Play(true);
        }

        private static void SpawnShards(Transform parent, Color tint, Color highlightTint, int shardCount, float lifetime, float intensity)
        {
            Material shardMaterial = CreateShardMaterial(Color.Lerp(tint, highlightTint, 0.34f));
            Mesh shardMesh = GetSphereMesh();

            for (int i = 0; i < shardCount; i++)
            {
                GameObject shard = new GameObject("Shard");
                shard.transform.SetParent(parent, false);

                float size = Random.Range(0.055f, 0.14f);
                Vector3 randomDirection = Random.onUnitSphere;
                if (randomDirection.sqrMagnitude < 0.001f)
                {
                    randomDirection = Vector3.up;
                }

                shard.transform.localPosition = randomDirection * Random.Range(0.04f, 0.22f);
                shard.transform.localRotation = Random.rotationUniform;
                shard.transform.localScale = Vector3.one * size;

                MeshFilter meshFilter = shard.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = shardMesh;

                MeshRenderer meshRenderer = shard.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterial = shardMaterial;

                SphereCollider collider = shard.AddComponent<SphereCollider>();
                collider.radius = 0.5f;

                Rigidbody rb = shard.AddComponent<Rigidbody>();
                rb.useGravity = true;
                rb.mass = 0.05f;
                rb.linearDamping = 0.35f;
                rb.angularDamping = 0.2f;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                float outwardSpeed = Random.Range(6.6f, 14.8f) * intensity;
                Vector3 burstDirection = (randomDirection + Vector3.up * 0.28f).normalized;
                rb.linearVelocity = burstDirection * outwardSpeed + Vector3.up * Random.Range(0.8f, 2.4f);
                rb.angularVelocity = Random.onUnitSphere * Random.Range(11f, 28f);

                Object.Destroy(shard, lifetime);
            }
        }

        private static void SpawnFlashLight(Transform parent, Color tint, Color highlightTint, float lifetime, float intensity)
        {
            GameObject lightObject = new GameObject("DeathFlashLight");
            lightObject.transform.SetParent(parent, false);
            lightObject.transform.localPosition = Vector3.up * 0.15f;

            Light flashLight = lightObject.AddComponent<Light>();
            flashLight.type = LightType.Point;
            flashLight.range = Mathf.Lerp(6.5f, 12.5f, (intensity - 1f) / 2f);
            flashLight.intensity = Mathf.Lerp(7f, 15f, (intensity - 1f) / 2f);
            flashLight.color = Color.Lerp(tint, highlightTint, 0.55f);
            flashLight.shadows = LightShadows.None;

            LightPulse pulse = lightObject.AddComponent<LightPulse>();
            pulse.Target = flashLight;
            pulse.Duration = Mathf.Min(0.26f, lifetime * 0.35f);
        }

        private static Material CreateShardMaterial(Color tint)
        {
            Shader shader = ChooseSurfaceShader();
            Material material = new Material(shader)
            {
                name = "DeathShardMat",
                color = tint
            };

            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", tint);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", tint);
            }

            if (material.HasProperty("_ShadowColor"))
            {
                material.SetColor("_ShadowColor", Color.Lerp(tint, new Color(0.03f, 0.02f, 0.05f, 1f), 0.56f));
            }

            if (material.HasProperty("_HighlightColor"))
            {
                material.SetColor("_HighlightColor", Color.Lerp(tint, Color.white, 0.42f));
            }

            if (material.HasProperty("_InkColor"))
            {
                material.SetColor("_InkColor", new Color(0.008f, 0.006f, 0.014f, 1f));
            }

            if (material.HasProperty("_InkWidth"))
            {
                material.SetFloat("_InkWidth", 2.05f);
            }

            if (material.HasProperty("_ShadeSteps"))
            {
                material.SetFloat("_ShadeSteps", 3f);
            }

            if (material.HasProperty("_HalftoneStrength"))
            {
                material.SetFloat("_HalftoneStrength", 0f);
            }

            if (material.HasProperty("_CastShadowPatternStrength"))
            {
                material.SetFloat("_CastShadowPatternStrength", 0f);
            }

            if (material.HasProperty("_PatchStrength"))
            {
                material.SetFloat("_PatchStrength", 0.16f);
            }

            if (material.HasProperty("_PatchScale"))
            {
                material.SetFloat("_PatchScale", 2.5f);
            }

            if (material.HasProperty("_PatchSoftness"))
            {
                material.SetFloat("_PatchSoftness", 0.7f);
            }

            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", tint * 1.6f);
            }

            return material;
        }

        private static Material CreateParticleMaterial(Color tint)
        {
            Shader shader = FindFirstSupportedShader(
                "Sumo/ComicTransparent",
                "Universal Render Pipeline/Particles/Unlit",
                "Particles/Standard Unlit",
                "Sprites/Default",
                "Unlit/Color",
                "Standard");

            Material material = new Material(shader)
            {
                name = "DeathParticleMat",
                color = tint
            };

            ConfigureTransparent(material);
            return material;
        }

        private static Shader ChooseSurfaceShader()
        {
            bool hasRenderPipeline = GraphicsSettings.currentRenderPipeline != null;
            return hasRenderPipeline
                ? FindFirstSupportedShader(
                    "Sumo/ComicToon",
                    "Universal Render Pipeline/Lit",
                    "Universal Render Pipeline/Simple Lit",
                    "Universal Render Pipeline/Unlit",
                    "Standard")
                : FindFirstSupportedShader(
                    "Standard",
                    "Legacy Shaders/Diffuse",
                    "Unlit/Color");
        }

        private static Shader FindFirstSupportedShader(params string[] shaderNames)
        {
            if (shaderNames != null)
            {
                for (int i = 0; i < shaderNames.Length; i++)
                {
                    string name = shaderNames[i];
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    Shader shader = Shader.Find(name);
                    if (shader != null && shader.isSupported)
                    {
                        return shader;
                    }
                }
            }

            Shader fallback = Shader.Find("Standard");
            if (fallback != null)
            {
                return fallback;
            }

            return Shader.Find("Hidden/InternalErrorShader");
        }

        private static void ConfigureTransparent(Material material)
        {
            if (material == null)
            {
                return;
            }

            if (material.HasProperty("_Surface"))
            {
                material.SetFloat("_Surface", 1f);
            }

            if (material.HasProperty("_Blend"))
            {
                material.SetFloat("_Blend", 0f);
            }

            if (material.HasProperty("_ZWrite"))
            {
                material.SetFloat("_ZWrite", 0f);
            }

            if (material.HasProperty("_SrcBlend"))
            {
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            }

            if (material.HasProperty("_DstBlend"))
            {
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            }

            if (material.HasProperty("_Mode"))
            {
                material.SetFloat("_Mode", 3f);
            }

            Color tint = material.color;
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", tint);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", tint);
            }

            if (material.HasProperty("_ShadowColor"))
            {
                material.SetColor("_ShadowColor", Color.Lerp(tint, new Color(0.02f, 0.018f, 0.04f, tint.a), 0.52f));
            }

            if (material.HasProperty("_HighlightColor"))
            {
                material.SetColor("_HighlightColor", Color.Lerp(tint, Color.white, 0.42f));
            }

            if (material.HasProperty("_InkColor"))
            {
                material.SetColor("_InkColor", new Color(0.008f, 0.007f, 0.014f, Mathf.Clamp01(tint.a + 0.44f)));
            }

            if (material.HasProperty("_InkWidth"))
            {
                material.SetFloat("_InkWidth", 1.85f);
            }

            if (material.HasProperty("_ShadeSteps"))
            {
                material.SetFloat("_ShadeSteps", 3f);
            }

            if (material.HasProperty("_HalftoneStrength"))
            {
                material.SetFloat("_HalftoneStrength", 0f);
            }

            if (material.HasProperty("_CastShadowPatternStrength"))
            {
                material.SetFloat("_CastShadowPatternStrength", 0f);
            }

            if (material.HasProperty("_PatchStrength"))
            {
                material.SetFloat("_PatchStrength", 0.12f);
            }

            if (material.HasProperty("_PatchScale"))
            {
                material.SetFloat("_PatchScale", 2.8f);
            }

            if (material.HasProperty("_PatchSoftness"))
            {
                material.SetFloat("_PatchSoftness", 0.72f);
            }

            material.EnableKeyword("_ALPHABLEND_ON");
            material.renderQueue = (int)RenderQueue.Transparent;
        }

        private static Mesh GetSphereMesh()
        {
            if (_sphereMesh != null)
            {
                return _sphereMesh;
            }

            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            MeshFilter filter = temp.GetComponent<MeshFilter>();
            _sphereMesh = filter != null ? filter.sharedMesh : null;

            Collider col = temp.GetComponent<Collider>();
            if (col != null)
            {
                if (Application.isPlaying)
                {
                    Object.Destroy(col);
                }
                else
                {
                    Object.DestroyImmediate(col);
                }
            }

            if (Application.isPlaying)
            {
                Object.Destroy(temp);
            }
            else
            {
                Object.DestroyImmediate(temp);
            }

            return _sphereMesh;
        }

        private sealed class TimedDestroy : MonoBehaviour
        {
            public float Lifetime { get; set; } = 2f;

            private void OnEnable()
            {
                Destroy(gameObject, Mathf.Max(0.1f, Lifetime));
            }
        }

        private sealed class LightPulse : MonoBehaviour
        {
            public Light Target { get; set; }
            public float Duration { get; set; } = 0.2f;

            private float _startIntensity;
            private float _elapsed;

            private void OnEnable()
            {
                _elapsed = 0f;
                if (Target != null)
                {
                    _startIntensity = Target.intensity;
                }
            }

            private void Update()
            {
                if (Target == null)
                {
                    return;
                }

                _elapsed += Time.deltaTime;
                float duration = Mathf.Max(0.01f, Duration);
                float t = Mathf.Clamp01(_elapsed / duration);
                float fade = 1f - t * t;
                Target.intensity = _startIntensity * fade;
            }
        }
    }
}

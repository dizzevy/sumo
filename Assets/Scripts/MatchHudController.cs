using Fusion;
using UnityEngine;
using UnityEngine.UI;

namespace Sumo.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(PlayerRoundState))]
    public sealed class MatchHudController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private NetworkObject networkObject;
        [SerializeField] private PlayerRoundState playerRoundState;
        [SerializeField] private MatchRoundManager matchRoundManager;
        [SerializeField] private Sumo.SumoBallController ballController;

        [Header("Runtime UI")]
        [SerializeField] private Canvas canvasRoot;
        [SerializeField] private Text primaryText;
        [SerializeField] private Text secondaryText;
        [SerializeField] private Text alertText;
        [SerializeField] private Text abilityText;
        [SerializeField] private Image abilityFillImage;
        [SerializeField] private bool autoCreateCanvas = true;
        [SerializeField] private int sortingOrder = 450;

        [Header("Layout")]
        [SerializeField] private Vector2 panelSize = new Vector2(760f, 220f);
        [SerializeField] private Vector2 panelOffset = new Vector2(0f, -14f);

        [Header("Messages")]
        [SerializeField] private float alertMessageDuration = 2.1f;

        private Canvas _runtimeCanvas;
        private GameObject _abilityPanel;
        private bool _uiReady;
        private int _lastEliminationSequenceSeen = -1;
        private int _lastWinnerSequenceSeen = -1;
        private float _alertUntil;
        private string _alertMessage;
        private bool _lastSpectatorState;
        private bool _spectatorStateKnown;
        private bool _returnToMenuRequested;
        private bool _abilityVisualInitialized;
        private bool _lastAbilityNetworkActive;
        private float _abilityVisualStamina01 = 1f;
        private Sumo.SumoPlayerClass _abilityVisualClass = Sumo.SumoPlayerClass.None;
        private static Sprite _staminaGradientSprite;

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
                DisableUi();
                return;
            }

            CacheReferencesIfNeeded();
            ResolveManagerIfNeeded();

            MatchState currentState = MatchState.WaitingForPlayers;
            bool hasRoundState = matchRoundManager != null && matchRoundManager.TryGetState(out currentState);
            if (hasRoundState && currentState == MatchState.ClassSelection)
            {
                DisableUi();
                return;
            }

            if (!_uiReady)
            {
                EnsureUi();
                if (!_uiReady)
                {
                    return;
                }
            }

            if (!hasRoundState)
            {
                primaryText.text = "Connecting...";
                secondaryText.text = string.Empty;
                alertText.text = string.Empty;
                if (_abilityPanel != null)
                {
                    _abilityPanel.SetActive(false);
                }

                return;
            }

            UpdateMainTexts(currentState);
            UpdateAbilityUi(currentState);
            UpdateNotifications();
            UpdateAlertText();
        }

        private void UpdateMainTexts(MatchState state)
        {
            int seconds = Mathf.Max(0, Mathf.CeilToInt(matchRoundManager.RemainingPhaseTime));
            int alive = Mathf.Max(0, matchRoundManager.AlivePlayersInRound);
            int round = Mathf.Max(0, matchRoundManager.RoundIndex);
            int activePlayers = CountActivePlayers();
            int minPlayers = Mathf.Max(2, matchRoundManager.MinimumPlayersToStart);
            int maxPlayers = Mathf.Max(minPlayers, matchRoundManager.MaximumPlayersPerMatch);
            bool isLobbyCountdown = matchRoundManager.IsWaitingForMorePlayersCountdownActive;
            int lobbyCountdownSeconds = Mathf.Max(0, Mathf.CeilToInt(matchRoundManager.WaitingForMorePlayersRemainingTime));
            bool spectator = playerRoundState != null && playerRoundState.IsSpectator && !playerRoundState.IsAlive;

            switch (state)
            {
                case MatchState.WaitingForPlayers:
                    if (activePlayers < minPlayers)
                    {
                        primaryText.text = $"Waiting players: {activePlayers}/{maxPlayers} (min {minPlayers})";
                    }
                    else if (isLobbyCountdown)
                    {
                        primaryText.text = $"Players: {activePlayers}/{maxPlayers} | Start in: {lobbyCountdownSeconds}s";
                    }
                    else
                    {
                        primaryText.text = $"Players: {activePlayers}/{maxPlayers}";
                    }

                    break;

                case MatchState.ClassSelection:
                    primaryText.text = $"Choose class: {seconds}s";
                    break;

                case MatchState.PreRoundBox:
                    primaryText.text = $"Round {round}: preparing";
                    break;

                case MatchState.Countdown:
                    primaryText.text = $"Start in: {seconds}";
                    break;

                case MatchState.DropPlayers:
                    primaryText.text = "Drop!";
                    break;

                case MatchState.SafeZonePhase:
                    primaryText.text = $"Zone {matchRoundManager.CurrentZoneStep}: {seconds}s";
                    break;

                case MatchState.EliminateOutsideZone:
                    primaryText.text = "Checking zone...";
                    break;

                case MatchState.NextZone:
                    primaryText.text = $"Next zone: {matchRoundManager.CurrentZoneStep}";
                    break;

                case MatchState.RoundFinished:
                    primaryText.text = $"Round finished. Next in: {seconds}";
                    break;

                case MatchState.Scoreboard:
                    primaryText.text = $"Scoreboard | Next round in: {seconds}s";
                    break;

                case MatchState.ResetRound:
                    primaryText.text = "Resetting round...";
                    break;

                case MatchState.MatchFinished:
                    primaryText.text = $"Final score | Menu in: {seconds}s";
                    break;

                default:
                    primaryText.text = state.ToString();
                    break;
            }

            string modeText = spectator ? "SPECTATING" : "PLAYING";
            if (state == MatchState.Scoreboard || state == MatchState.MatchFinished)
            {
                secondaryText.text = BuildScoreboardText(state == MatchState.MatchFinished);
                TryReturnToMainMenuAfterFinalScore(state);
            }
            else if (state == MatchState.ClassSelection)
            {
                bool ready = playerRoundState != null && playerRoundState.IsClientReady;
                secondaryText.text = ready ? "Class locked. Waiting for others." : "Select a class and confirm.";
                _returnToMenuRequested = false;
            }
            else
            {
                secondaryText.text = $"Alive: {alive}   |   {modeText}";
                _returnToMenuRequested = false;
            }

            if (!_spectatorStateKnown)
            {
                _lastSpectatorState = spectator;
                _spectatorStateKnown = true;
            }
            else if (spectator && !_lastSpectatorState)
            {
                ShowAlert("You are eliminated. Spectating.");
            }

            _lastSpectatorState = spectator;
        }

        private void UpdateAbilityUi(MatchState matchState)
        {
            if (_abilityPanel == null || abilityText == null || abilityFillImage == null)
            {
                return;
            }

            if (ballController == null)
            {
                ballController = GetComponent<Sumo.SumoBallController>();
            }

            Sumo.SumoPlayerClass playerClass = ballController != null
                ? ballController.AuthoritativeClass
                : Sumo.SumoPlayerClass.None;
            if (playerClass == Sumo.SumoPlayerClass.None && ballController != null)
            {
                playerClass = ballController.CurrentClass;
            }

            bool show = ballController != null
                        && playerClass != Sumo.SumoPlayerClass.None
                        && matchRoundManager != null
                        && matchState != MatchState.ClassSelection;

            _abilityPanel.SetActive(show);
            if (!show)
            {
                ResetAbilityVisualState();
                return;
            }

            Sumo.SumoPlayerClassDefinition definition = Sumo.SumoPlayerClassCatalog.GetDefinition(playerClass);
            float networkStamina = Mathf.Clamp01(ballController.AuthoritativeAbilityStamina01);
            bool active = ballController.AuthoritativeAbilityActive;
            bool unlocked = matchRoundManager == null || matchRoundManager.ClassAbilitiesUnlocked;
            if (!unlocked)
            {
                ResetAbilityVisualState();
                abilityText.text = $"{definition.DisplayName}: {definition.AbilityName} | Locked until first zone";
                abilityFillImage.fillAmount = 1f;
                abilityFillImage.color = new Color(0.65f, 0.72f, 0.82f, 1f);
                return;
            }

            float stamina = UpdateAbilityVisualStamina(definition, active, networkStamina);
            bool ready = stamina >= 0.999f && !active;
            int seconds = 0;
            if (active)
            {
                seconds = Mathf.Max(0, Mathf.CeilToInt(stamina * definition.AbilityActiveSeconds));
            }
            else if (!ready)
            {
                seconds = Mathf.Max(0, Mathf.CeilToInt((1f - stamina) * definition.AbilityRechargeSeconds));
            }

            string abilityState = active
                ? $"Active: {seconds}s left"
                : (ready ? "Ready (F)" : $"Recharge: {seconds}s");

            abilityText.text = $"{definition.DisplayName}: {definition.AbilityName} | {abilityState}";
            abilityFillImage.fillAmount = stamina;
            abilityFillImage.color = Color.white;
        }

        private float UpdateAbilityVisualStamina(Sumo.SumoPlayerClassDefinition definition, bool networkActive, float networkStamina)
        {
            if (!_abilityVisualInitialized || _abilityVisualClass != definition.Class)
            {
                _abilityVisualInitialized = true;
                _abilityVisualClass = definition.Class;
                _abilityVisualStamina01 = networkActive ? 1f : Mathf.Clamp01(networkStamina);
                _lastAbilityNetworkActive = networkActive;
            }

            float deltaTime = Mathf.Max(0f, Time.deltaTime);
            if (networkActive)
            {
                if (!_lastAbilityNetworkActive)
                {
                    _abilityVisualStamina01 = 1f;
                }

                float activeSeconds = Mathf.Max(0.01f, definition.AbilityActiveSeconds);
                _abilityVisualStamina01 = Mathf.Max(0f, _abilityVisualStamina01 - deltaTime / activeSeconds);
            }
            else
            {
                if (_lastAbilityNetworkActive)
                {
                    _abilityVisualStamina01 = 0f;
                }

                if (networkStamina >= 0.999f)
                {
                    _abilityVisualStamina01 = 1f;
                }
                else
                {
                    float rechargeSeconds = Mathf.Max(0.01f, definition.AbilityRechargeSeconds);
                    _abilityVisualStamina01 = Mathf.Min(1f, _abilityVisualStamina01 + deltaTime / rechargeSeconds);
                    _abilityVisualStamina01 = Mathf.Max(_abilityVisualStamina01, Mathf.Clamp01(networkStamina));
                }
            }

            _lastAbilityNetworkActive = networkActive;
            return Mathf.Clamp01(_abilityVisualStamina01);
        }

        private void ResetAbilityVisualState()
        {
            _abilityVisualInitialized = false;
            _lastAbilityNetworkActive = false;
            _abilityVisualStamina01 = 1f;
            _abilityVisualClass = Sumo.SumoPlayerClass.None;
        }

        private void UpdateNotifications()
        {
            int eliminationSeq = matchRoundManager.LocalEliminationNotificationSequence;
            if (_lastEliminationSequenceSeen != eliminationSeq)
            {
                if (_lastEliminationSequenceSeen >= 0)
                {
                    string player = FormatPlayerLabel(matchRoundManager.LastNotifiedEliminatedPlayerRawEncoded);
                    int aliveLeft = Mathf.Max(0, matchRoundManager.LastNotifiedRemainingAlive);
                    ShowAlert($"{player} eliminated. Alive: {aliveLeft}");
                }

                _lastEliminationSequenceSeen = eliminationSeq;
            }

            int winnerSeq = matchRoundManager.LocalWinnerNotificationSequence;
            if (_lastWinnerSequenceSeen != winnerSeq)
            {
                if (_lastWinnerSequenceSeen >= 0)
                {
                    string winner = FormatPlayerLabel(matchRoundManager.LastNotifiedWinnerRawEncoded);
                    ShowAlert($"Winner: {winner}");
                }

                _lastWinnerSequenceSeen = winnerSeq;
            }
        }

        private void UpdateAlertText()
        {
            if (Time.unscaledTime < _alertUntil && !string.IsNullOrWhiteSpace(_alertMessage))
            {
                alertText.text = _alertMessage;
            }
            else
            {
                alertText.text = string.Empty;
            }
        }

        private void ShowAlert(string message)
        {
            _alertMessage = message;
            _alertUntil = Time.unscaledTime + Mathf.Max(0.65f, alertMessageDuration);
        }

        private bool IsLocalPlayer()
        {
            return networkObject != null
                   && networkObject.Runner != null
                   && networkObject.Runner.IsRunning
                   && networkObject.HasInputAuthority;
        }

        private int CountActivePlayers()
        {
            if (matchRoundManager == null || matchRoundManager.Runner == null)
            {
                return 0;
            }

            int count = 0;
            foreach (PlayerRef _ in matchRoundManager.Runner.ActivePlayers)
            {
                count++;
            }

            return count;
        }

        private string FormatPlayerLabel(int rawEncoded)
        {
            if (matchRoundManager != null)
            {
                return matchRoundManager.FormatParticipantLabel(rawEncoded);
            }

            if (rawEncoded < 0)
            {
                return $"Bot {Mathf.Abs(rawEncoded)}";
            }

            if (rawEncoded == PlayerRef.None.RawEncoded)
            {
                return "Player";
            }

            if (matchRoundManager != null && matchRoundManager.Runner != null)
            {
                foreach (PlayerRef player in matchRoundManager.Runner.ActivePlayers)
                {
                    if (player.RawEncoded == rawEncoded)
                    {
                        return $"Player {player.PlayerId}";
                    }
                }
            }

            return $"Player {rawEncoded}";
        }

        private string BuildScoreboardText(bool includeWinner)
        {
            if (matchRoundManager == null || matchRoundManager.ScoreParticipantCount <= 0)
            {
                return string.Empty;
            }

            int count = Mathf.Min(MatchRoundManager.ScoreCapacity, matchRoundManager.ScoreParticipantCount);
            int target = Mathf.Max(1, matchRoundManager.MatchWinTarget);
            System.Text.StringBuilder builder = new System.Text.StringBuilder(128);

            if (includeWinner && matchRoundManager.MatchWinnerRawEncoded != PlayerRef.None.RawEncoded)
            {
                builder.Append("Winner: ");
                builder.Append(matchRoundManager.FormatParticipantLabel(matchRoundManager.MatchWinnerRawEncoded));
                builder.AppendLine();
            }

            for (int i = 0; i < count; i++)
            {
                int raw = matchRoundManager.GetScoreParticipantRawEncoded(i);
                int wins = matchRoundManager.GetScoreParticipantWins(i);
                builder.Append(matchRoundManager.FormatParticipantLabel(raw));
                builder.Append(": ");
                builder.Append(wins);
                builder.Append("/");
                builder.Append(target);

                if (i < count - 1)
                {
                    builder.Append((i + 1) % 4 == 0 ? "\n" : "   ");
                }
            }

            return builder.ToString();
        }

        private async void TryReturnToMainMenuAfterFinalScore(MatchState state)
        {
            if (_returnToMenuRequested
                || state != MatchState.MatchFinished
                || matchRoundManager == null
                || !matchRoundManager.ShouldReturnToMainMenuAfterMatch
                || matchRoundManager.RemainingPhaseTime > 0.05f)
            {
                return;
            }

            _returnToMenuRequested = true;

            Sumo.Online.MatchmakingClient matchmakingClient = FindObjectOfType<Sumo.Online.MatchmakingClient>(true);
            if (matchmakingClient == null)
            {
                _returnToMenuRequested = false;
                return;
            }

            try
            {
                await matchmakingClient.ReturnToMainMenuAsync();
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                _returnToMenuRequested = false;
            }
        }

        private void EnsureUi()
        {
            if (canvasRoot == null && autoCreateCanvas)
            {
                CreateRuntimeCanvas();
            }

            if (canvasRoot == null || primaryText == null || secondaryText == null || alertText == null)
            {
                _uiReady = false;
                return;
            }

            canvasRoot.gameObject.SetActive(true);
            _uiReady = true;
        }

        private void CreateRuntimeCanvas()
        {
            GameObject canvasObject = new GameObject($"{name}_MatchHudCanvas");
            _runtimeCanvas = canvasObject.AddComponent<Canvas>();
            _runtimeCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _runtimeCanvas.sortingOrder = sortingOrder;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();

            Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font == null)
            {
                font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }

            GameObject panel = new GameObject("HudTopCenterPanel");
            panel.transform.SetParent(canvasObject.transform, false);
            RectTransform panelRect = panel.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 1f);
            panelRect.anchorMax = new Vector2(0.5f, 1f);
            panelRect.pivot = new Vector2(0.5f, 1f);
            panelRect.anchoredPosition = panelOffset;
            panelRect.sizeDelta = panelSize;

            Image background = panel.AddComponent<Image>();
            background.color = new Color(0.02f, 0.05f, 0.09f, 0.34f);

            primaryText = CreateText(
                "Primary",
                panelRect,
                font,
                32,
                FontStyle.Bold,
                new Vector2(0f, -14f),
                new Vector2(panelSize.x - 18f, 38f),
                new Color(0.95f, 0.98f, 1f, 1f));

            secondaryText = CreateText(
                "Secondary",
                panelRect,
                font,
                22,
                FontStyle.Normal,
                new Vector2(0f, -64f),
                new Vector2(panelSize.x - 18f, 126f),
                new Color(0.82f, 0.92f, 1f, 0.98f));

            alertText = CreateText(
                "Alert",
                panelRect,
                font,
                21,
                FontStyle.Bold,
                new Vector2(0f, -186f),
                new Vector2(panelSize.x - 24f, 24f),
                new Color(1f, 0.9f, 0.62f, 1f));

            CreateAbilityPanel(canvasObject.transform, font);

            canvasRoot = _runtimeCanvas;
        }

        private void CreateAbilityPanel(Transform parent, Font font)
        {
            _abilityPanel = new GameObject("AbilityPanel");
            _abilityPanel.transform.SetParent(parent, false);

            RectTransform panelRect = _abilityPanel.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0f);
            panelRect.anchorMax = new Vector2(0.5f, 0f);
            panelRect.pivot = new Vector2(0.5f, 0f);
            panelRect.anchoredPosition = new Vector2(0f, 34f);
            panelRect.sizeDelta = new Vector2(760f, 96f);

            Image background = _abilityPanel.AddComponent<Image>();
            background.color = new Color(0.02f, 0.05f, 0.09f, 0.74f);

            abilityText = CreateText(
                "AbilityText",
                panelRect,
                font,
                22,
                FontStyle.Bold,
                new Vector2(0f, -10f),
                new Vector2(728f, 30f),
                new Color(0.92f, 0.97f, 1f, 1f));

            GameObject barBackgroundObject = new GameObject("AbilityBarBackground");
            barBackgroundObject.transform.SetParent(panelRect, false);
            RectTransform barBackgroundRect = barBackgroundObject.AddComponent<RectTransform>();
            barBackgroundRect.anchorMin = new Vector2(0.5f, 0f);
            barBackgroundRect.anchorMax = new Vector2(0.5f, 0f);
            barBackgroundRect.pivot = new Vector2(0.5f, 0f);
            barBackgroundRect.anchoredPosition = new Vector2(0f, 18f);
            barBackgroundRect.sizeDelta = new Vector2(700f, 28f);

            Image barBackground = barBackgroundObject.AddComponent<Image>();
            barBackground.color = new Color(0f, 0f, 0f, 0.62f);

            GameObject fillObject = new GameObject("AbilityBarFill");
            fillObject.transform.SetParent(barBackgroundObject.transform, false);
            RectTransform fillRect = fillObject.AddComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;

            abilityFillImage = fillObject.AddComponent<Image>();
            abilityFillImage.sprite = GetStaminaGradientSprite();
            abilityFillImage.type = Image.Type.Filled;
            abilityFillImage.fillMethod = Image.FillMethod.Horizontal;
            abilityFillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
            abilityFillImage.fillAmount = 1f;
            abilityFillImage.color = Color.white;

            for (int i = 1; i < 10; i++)
            {
                GameObject tickObject = new GameObject($"AbilityBarTick{i}");
                tickObject.transform.SetParent(barBackgroundObject.transform, false);

                RectTransform tickRect = tickObject.AddComponent<RectTransform>();
                float x = i / 10f;
                tickRect.anchorMin = new Vector2(x, 0f);
                tickRect.anchorMax = new Vector2(x, 1f);
                tickRect.pivot = new Vector2(0.5f, 0.5f);
                tickRect.anchoredPosition = Vector2.zero;
                tickRect.sizeDelta = new Vector2(2f, 0f);

                Image tickImage = tickObject.AddComponent<Image>();
                tickImage.color = new Color(1f, 1f, 1f, 0.32f);
                tickImage.raycastTarget = false;
            }

            _abilityPanel.SetActive(false);
        }

        private static Sprite GetStaminaGradientSprite()
        {
            if (_staminaGradientSprite != null)
            {
                return _staminaGradientSprite;
            }

            const int width = 256;
            const int height = 24;
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "RuntimeStaminaGradient"
            };

            for (int x = 0; x < width; x++)
            {
                float t = x / (width - 1f);
                Color baseColor = EvaluateStaminaGradient(t);
                for (int y = 0; y < height; y++)
                {
                    float vertical = y / (height - 1f);
                    float highlight = Mathf.SmoothStep(0.55f, 1f, vertical) * 0.22f;
                    Color color = Color.Lerp(baseColor, Color.white, highlight);
                    color.a = 1f;
                    texture.SetPixel(x, y, color);
                }
            }

            texture.Apply(false, true);
            _staminaGradientSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, width, height),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect);
            return _staminaGradientSprite;
        }

        private static Color EvaluateStaminaGradient(float t)
        {
            Color empty = new Color(0.95f, 0.18f, 0.22f, 1f);
            Color low = new Color(1f, 0.58f, 0.14f, 1f);
            Color mid = new Color(1f, 0.9f, 0.22f, 1f);
            Color high = new Color(0.25f, 0.95f, 0.52f, 1f);
            Color full = new Color(0.16f, 0.72f, 1f, 1f);

            if (t < 0.25f)
            {
                return Color.Lerp(empty, low, t / 0.25f);
            }

            if (t < 0.55f)
            {
                return Color.Lerp(low, mid, (t - 0.25f) / 0.30f);
            }

            if (t < 0.82f)
            {
                return Color.Lerp(mid, high, (t - 0.55f) / 0.27f);
            }

            return Color.Lerp(high, full, (t - 0.82f) / 0.18f);
        }

        private static Text CreateText(
            string objectName,
            RectTransform parent,
            Font font,
            int fontSize,
            FontStyle style,
            Vector2 anchoredPosition,
            Vector2 size,
            Color color)
        {
            GameObject go = new GameObject(objectName);
            go.transform.SetParent(parent, false);

            RectTransform rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            Text text = go.AddComponent<Text>();
            text.font = font;
            text.fontSize = Mathf.Max(10, fontSize);
            text.fontStyle = style;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.color = color;

            Outline outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.88f);
            outline.effectDistance = new Vector2(1.1f, -1.1f);

            return text;
        }

        private void DisableUi()
        {
            if (_runtimeCanvas != null)
            {
                _runtimeCanvas.gameObject.SetActive(false);
            }

            if (canvasRoot != null)
            {
                canvasRoot.gameObject.SetActive(false);
            }

            if (_abilityPanel != null)
            {
                _abilityPanel.SetActive(false);
            }

            _uiReady = false;
            _lastEliminationSequenceSeen = -1;
            _lastWinnerSequenceSeen = -1;
            _spectatorStateKnown = false;
            _alertUntil = 0f;
            _alertMessage = string.Empty;
            ResetAbilityVisualState();
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

            if (ballController == null)
            {
                ballController = GetComponent<Sumo.SumoBallController>();
            }
        }

        private void OnValidate()
        {
            panelSize.x = Mathf.Max(320f, panelSize.x);
            panelSize.y = Mathf.Max(180f, panelSize.y);
            sortingOrder = Mathf.Clamp(sortingOrder, 0, 5000);
            alertMessageDuration = Mathf.Max(0.65f, alertMessageDuration);
            CacheReferencesIfNeeded();
        }
    }
}

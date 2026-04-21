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

        [Header("Runtime UI")]
        [SerializeField] private Canvas canvasRoot;
        [SerializeField] private Text primaryText;
        [SerializeField] private Text secondaryText;
        [SerializeField] private Text alertText;
        [SerializeField] private bool autoCreateCanvas = true;
        [SerializeField] private int sortingOrder = 450;

        [Header("Layout")]
        [SerializeField] private Vector2 panelSize = new Vector2(640f, 98f);
        [SerializeField] private Vector2 panelOffset = new Vector2(0f, -14f);

        [Header("Messages")]
        [SerializeField] private float alertMessageDuration = 2.1f;

        private Canvas _runtimeCanvas;
        private bool _uiReady;
        private int _lastEliminationSequenceSeen = -1;
        private int _lastWinnerSequenceSeen = -1;
        private float _alertUntil;
        private string _alertMessage;
        private bool _lastSpectatorState;
        private bool _spectatorStateKnown;

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

            if (!_uiReady)
            {
                EnsureUi();
                if (!_uiReady)
                {
                    return;
                }
            }

            if (matchRoundManager == null)
            {
                primaryText.text = "Connecting...";
                secondaryText.text = string.Empty;
                alertText.text = string.Empty;
                return;
            }

            UpdateMainTexts();
            UpdateNotifications();
            UpdateAlertText();
        }

        private void UpdateMainTexts()
        {
            MatchState state = matchRoundManager.State;
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

                case MatchState.ResetRound:
                    primaryText.text = "Resetting round...";
                    break;

                default:
                    primaryText.text = state.ToString();
                    break;
            }

            string modeText = spectator ? "SPECTATING" : "PLAYING";
            secondaryText.text = $"Alive: {alive}   |   {modeText}";

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
                new Vector2(0f, -50f),
                new Vector2(panelSize.x - 18f, 30f),
                new Color(0.82f, 0.92f, 1f, 0.98f));

            alertText = CreateText(
                "Alert",
                panelRect,
                font,
                21,
                FontStyle.Bold,
                new Vector2(0f, -78f),
                new Vector2(panelSize.x - 24f, 24f),
                new Color(1f, 0.9f, 0.62f, 1f));

            canvasRoot = _runtimeCanvas;
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

            _uiReady = false;
            _lastEliminationSequenceSeen = -1;
            _lastWinnerSequenceSeen = -1;
            _spectatorStateKnown = false;
            _alertUntil = 0f;
            _alertMessage = string.Empty;
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

        private void OnValidate()
        {
            panelSize.x = Mathf.Max(320f, panelSize.x);
            panelSize.y = Mathf.Max(80f, panelSize.y);
            sortingOrder = Mathf.Clamp(sortingOrder, 0, 5000);
            alertMessageDuration = Mathf.Max(0.65f, alertMessageDuration);
            CacheReferencesIfNeeded();
        }
    }
}

using System.Collections.Generic;
using System.Text;
using Fusion;
using UnityEngine;
using UnityEngine.UI;

namespace Sumo
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class SumoPlayerDebugOverlay : MonoBehaviour
    {
        [Header("Overlay")]
        [SerializeField] private bool autoCreateCanvas = true;
        [SerializeField] private Vector2 anchoredPosition = new Vector2(18f, -18f);
        [SerializeField] private int fontSize = 18;
        [SerializeField] private int sortingOrder = 260;
        [SerializeField] private Color textColor = new Color(1f, 0.97f, 0.84f, 1f);
        [SerializeField] private bool showPlayerRoster = true;
        [SerializeField] private float refreshInterval = 0.2f;

        private readonly List<PlayerRef> _players = new List<PlayerRef>(8);
        private readonly StringBuilder _builder = new StringBuilder(256);

        private NetworkObject _networkObject;
        private Canvas _runtimeCanvas;
        private Text _overlayText;
        private float _nextRefreshTime;

        private void Awake()
        {
            CacheComponents();
        }

        private void OnEnable()
        {
            CacheComponents();
            _nextRefreshTime = 0f;
        }

        private void OnDisable()
        {
            if (_runtimeCanvas != null)
            {
                Destroy(_runtimeCanvas.gameObject);
            }

            _runtimeCanvas = null;
            _overlayText = null;
        }

        private void LateUpdate()
        {
            if (!ShouldShowOverlay())
            {
                SetOverlayVisible(false);
                return;
            }

            EnsureOverlay();
            if (_overlayText == null)
            {
                return;
            }

            SetOverlayVisible(true);

            if (Time.unscaledTime < _nextRefreshTime)
            {
                return;
            }

            _nextRefreshTime = Time.unscaledTime + Mathf.Max(0.05f, refreshInterval);
            _overlayText.text = BuildOverlayText();
        }

        private void CacheComponents()
        {
            if (_networkObject == null)
            {
                _networkObject = GetComponent<NetworkObject>();
            }
        }

        private bool ShouldShowOverlay()
        {
            if (!autoCreateCanvas)
            {
                return false;
            }

            if (Application.isBatchMode)
            {
                return false;
            }

            if (_networkObject == null || _networkObject.Runner == null || !_networkObject.Runner.IsRunning)
            {
                return false;
            }

            return _networkObject.HasInputAuthority;
        }

        private void EnsureOverlay()
        {
            if (_runtimeCanvas != null && _overlayText != null)
            {
                return;
            }

            GameObject canvasObject = new GameObject($"{name}_PlayerDebugOverlay");
            _runtimeCanvas = canvasObject.AddComponent<Canvas>();
            _runtimeCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _runtimeCanvas.sortingOrder = sortingOrder;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();

            GameObject textObject = new GameObject("PlayerDebugText");
            textObject.transform.SetParent(canvasObject.transform, false);

            RectTransform rect = textObject.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = new Vector2(420f, 180f);

            Text text = textObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = Mathf.Max(12, fontSize);
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.color = textColor;
            text.raycastTarget = false;

            Outline outline = textObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
            outline.effectDistance = new Vector2(1f, -1f);

            _overlayText = text;
        }

        private void SetOverlayVisible(bool visible)
        {
            if (_runtimeCanvas != null)
            {
                _runtimeCanvas.gameObject.SetActive(visible);
            }
        }

        private string BuildOverlayText()
        {
            _players.Clear();
            if (_networkObject != null && _networkObject.Runner != null)
            {
                foreach (PlayerRef player in _networkObject.Runner.ActivePlayers)
                {
                    _players.Add(player);
                }

                _players.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
            }

            _builder.Clear();
            _builder.Append("Player Debug");
            _builder.Append('\n');

            PlayerRef localPlayer = _networkObject != null ? _networkObject.InputAuthority : PlayerRef.None;
            _builder.Append("Local: Player ");
            _builder.Append(GetPlayerNumber(localPlayer));
            if (localPlayer != PlayerRef.None)
            {
                _builder.Append(" [id ");
                _builder.Append(GetFusionPlayerId(localPlayer));
                _builder.Append(']');
            }
            _builder.Append(" (");
            _builder.Append(GetRunnerRole());
            _builder.Append(')');

            if (_networkObject != null)
            {
                _builder.Append('\n');
                _builder.Append("Authority: ");
                bool wroteAuthority = false;

                if (_networkObject.HasInputAuthority)
                {
                    _builder.Append("Input");
                    wroteAuthority = true;
                }

                if (_networkObject.HasStateAuthority)
                {
                    if (wroteAuthority)
                    {
                        _builder.Append(" + ");
                    }

                    _builder.Append("State");
                    wroteAuthority = true;
                }

                if (!wroteAuthority)
                {
                    _builder.Append("Remote");
                }
            }

            if (!showPlayerRoster || _networkObject == null || _networkObject.Runner == null)
            {
                return _builder.ToString();
            }

            _builder.Append('\n');
            _builder.Append("Players:");

            for (int i = 0; i < _players.Count; i++)
            {
                PlayerRef player = _players[i];
                _builder.Append('\n');
                _builder.Append("P");
                _builder.Append(GetPlayerNumber(player));
                _builder.Append(" [id ");
                _builder.Append(GetFusionPlayerId(player));
                _builder.Append(']');

                if (player == localPlayer)
                {
                    _builder.Append(" local");
                }

                if (_networkObject.Runner.TryGetPlayerObject(player, out NetworkObject playerObject) && playerObject != null)
                {
                    if (playerObject.HasInputAuthority)
                    {
                        _builder.Append(" IA");
                    }

                    if (playerObject.HasStateAuthority)
                    {
                        _builder.Append(" SA");
                    }
                }
            }

            return _builder.ToString();
        }

        private string GetRunnerRole()
        {
            if (_networkObject == null || _networkObject.Runner == null)
            {
                return "Offline";
            }

            NetworkRunner runner = _networkObject.Runner;
            if (runner.IsServer && runner.IsClient)
            {
                return "Host";
            }

            if (runner.IsServer)
            {
                return "Server";
            }

            if (runner.IsClient)
            {
                return "Client";
            }

            return "Offline";
        }

        private int GetPlayerNumber(PlayerRef player)
        {
            if (player == PlayerRef.None)
            {
                return 0;
            }

            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i] == player)
                {
                    return i + 1;
                }
            }

            return _players.Count + 1;
        }

        private static int GetFusionPlayerId(PlayerRef player)
        {
            return player.PlayerId > 0 ? player.PlayerId : player.RawEncoded;
        }
    }
}

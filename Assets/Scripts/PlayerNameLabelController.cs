using Fusion;
using UnityEngine;
using UnityEngine.UI;

namespace Sumo.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerRoundState))]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class PlayerNameLabelController : MonoBehaviour
    {
        [SerializeField] private float labelHeight = 1.55f;
        [SerializeField] private Vector2 labelSize = new Vector2(220f, 44f);
        [SerializeField] private float worldScale = 0.012f;
        [SerializeField] private int fontSize = 24;
        [SerializeField] private int sortingOrder = 80;
        [SerializeField] private float labelRefreshInterval = 0.25f;

        private static bool _labelsVisible;
        private static int _lastToggleFrame = -1;

        private PlayerRoundState _roundState;
        private NetworkObject _networkObject;
        private MatchRoundManager _roundManager;
        private Canvas _canvas;
        private Text _labelText;
        private float _nextLabelRefreshTime;

        private void Awake()
        {
            CacheReferences();
        }

        private void Update()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            CacheReferences();
            HandleToggleInput();
            EnsureLabel();
            RefreshLabelTextIfNeeded();
            UpdateLabelPoseAndVisibility();
        }

        private void OnDestroy()
        {
            if (_canvas != null)
            {
                Destroy(_canvas.gameObject);
            }
        }

        private void CacheReferences()
        {
            if (_roundState == null)
            {
                _roundState = GetComponent<PlayerRoundState>();
            }

            if (_networkObject == null)
            {
                _networkObject = GetComponent<NetworkObject>();
            }

            if (_roundManager == null)
            {
                _roundManager = FindObjectOfType<MatchRoundManager>(true);
            }
        }

        private static void HandleToggleInput()
        {
            if (_lastToggleFrame == Time.frameCount)
            {
                return;
            }

            if (!Sumo.SumoNpcBallDriver.WasKeyPressedThisFrame(KeyCode.N))
            {
                return;
            }

            _lastToggleFrame = Time.frameCount;
            _labelsVisible = !_labelsVisible;
        }

        private void EnsureLabel()
        {
            if (_canvas != null)
            {
                return;
            }

            GameObject canvasObject = new GameObject("PlayerNameLabel");
            canvasObject.transform.SetParent(transform, false);

            _canvas = canvasObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.sortingOrder = sortingOrder;

            RectTransform canvasRect = canvasObject.GetComponent<RectTransform>();
            canvasRect.sizeDelta = labelSize;
            canvasRect.localScale = Vector3.one * Mathf.Max(0.001f, worldScale);

            GameObject textObject = new GameObject("Text");
            textObject.transform.SetParent(canvasObject.transform, false);

            RectTransform textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            _labelText = textObject.AddComponent<Text>();
            _labelText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (_labelText.font == null)
            {
                _labelText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }

            _labelText.fontSize = Mathf.Max(10, fontSize);
            _labelText.fontStyle = FontStyle.Bold;
            _labelText.alignment = TextAnchor.MiddleCenter;
            _labelText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _labelText.verticalOverflow = VerticalWrapMode.Overflow;
            _labelText.color = Color.white;

            Outline outline = textObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.2f, -1.2f);

            _canvas.gameObject.SetActive(false);
        }

        private void RefreshLabelTextIfNeeded()
        {
            if (_labelText == null || Time.unscaledTime < _nextLabelRefreshTime)
            {
                return;
            }

            _nextLabelRefreshTime = Time.unscaledTime + Mathf.Max(0.05f, labelRefreshInterval);
            _labelText.text = ResolveLabelText();
        }

        private string ResolveLabelText()
        {
            if (_roundManager != null && _roundState != null)
            {
                return _roundManager.FormatParticipantLabel(_roundState);
            }

            if (_roundState != null && _roundState.ParticipantRawEncoded < 0)
            {
                return $"Bot {Mathf.Abs(_roundState.ParticipantRawEncoded)}";
            }

            if (_networkObject != null && _networkObject.InputAuthority != PlayerRef.None)
            {
                return $"Player {_networkObject.InputAuthority.PlayerId}";
            }

            return "Player";
        }

        private void UpdateLabelPoseAndVisibility()
        {
            if (_canvas == null)
            {
                return;
            }

            bool shouldShow = _labelsVisible
                              && _roundState != null
                              && _roundState.IsAlive
                              && _networkObject != null
                              && _networkObject.Runner != null;

            if (_canvas.gameObject.activeSelf != shouldShow)
            {
                _canvas.gameObject.SetActive(shouldShow);
            }

            if (!shouldShow)
            {
                return;
            }

            Transform canvasTransform = _canvas.transform;
            canvasTransform.position = transform.position + Vector3.up * Mathf.Max(0.1f, labelHeight);

            Camera camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            Vector3 toCamera = canvasTransform.position - camera.transform.position;
            if (toCamera.sqrMagnitude > 0.0001f)
            {
                canvasTransform.rotation = Quaternion.LookRotation(toCamera.normalized, Vector3.up);
            }
        }

        private void OnValidate()
        {
            labelHeight = Mathf.Max(0.1f, labelHeight);
            labelSize.x = Mathf.Max(80f, labelSize.x);
            labelSize.y = Mathf.Max(20f, labelSize.y);
            worldScale = Mathf.Max(0.001f, worldScale);
            fontSize = Mathf.Clamp(fontSize, 10, 72);
            labelRefreshInterval = Mathf.Max(0.05f, labelRefreshInterval);
        }
    }
}

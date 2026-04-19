using Fusion;
using UnityEngine;
using UnityEngine.UI;

namespace Sumo
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(SumoBallController))]
    [RequireComponent(typeof(Rigidbody))]
    public sealed class SumoSpeedometerUI : MonoBehaviour
    {
        [Header("UI")]
        [SerializeField] private Canvas canvasRoot;
        [SerializeField] private Text speedText;
        [SerializeField] private bool autoCreateCanvas = true;
        [SerializeField] private Vector2 anchoredPosition = new Vector2(0f, 58f);
        [SerializeField] private int fontSize = 42;
        [SerializeField] private Color textColor = Color.white;
        [SerializeField] private bool padWithLeadingZeros;

        [Header("Display")]
        [SerializeField] private float speedometerSmoothing = 11f;
        [SerializeField] private float kmhMultiplier = 3.6f;
        [SerializeField] private bool horizontalSpeedOnly = true;

        private NetworkObject _networkObject;
        private SumoBallController _ballController;
        private Rigidbody _rigidbody;
        private Canvas _runtimeCanvas;
        private Text _runtimeText;
        private bool _uiReady;
        private float _displayedSpeedKmh;

        private void Awake()
        {
            _networkObject = GetComponent<NetworkObject>();
            _ballController = GetComponent<SumoBallController>();
            _rigidbody = GetComponent<Rigidbody>();
        }

        private void OnDestroy()
        {
            if (_runtimeCanvas != null)
            {
                Destroy(_runtimeCanvas.gameObject);
            }

            _runtimeCanvas = null;
            _runtimeText = null;
            _uiReady = false;
        }

        private void Update()
        {
            if (!IsLocalPlayerObject())
            {
                DisableProxyUi();
                return;
            }

            if (!_uiReady)
            {
                EnsureUi();
                if (!_uiReady)
                {
                    return;
                }

                _displayedSpeedKmh = GetCurrentSpeedMps() * Mathf.Max(0.01f, kmhMultiplier);
                RefreshText(_displayedSpeedKmh);
            }

            float targetSpeedKmh = GetCurrentSpeedMps() * Mathf.Max(0.01f, kmhMultiplier);
            float blend = 1f - Mathf.Exp(-Mathf.Max(0.01f, speedometerSmoothing) * Time.unscaledDeltaTime);
            _displayedSpeedKmh = Mathf.Lerp(_displayedSpeedKmh, targetSpeedKmh, blend);
            RefreshText(_displayedSpeedKmh);
        }

        private bool IsLocalPlayerObject()
        {
            return _networkObject != null
                && _networkObject.Runner != null
                && _networkObject.Runner.IsRunning
                && _networkObject.HasInputAuthority;
        }

        private float GetCurrentSpeedMps()
        {
            Vector3 velocity;
            if (_rigidbody != null)
            {
                velocity = _rigidbody.linearVelocity;
            }
            else if (_ballController != null)
            {
                velocity = _ballController.CurrentVelocity;
            }
            else
            {
                return 0f;
            }

            if (horizontalSpeedOnly)
            {
                velocity.y = 0f;
            }

            return velocity.magnitude;
        }

        private void RefreshText(float speedKmh)
        {
            if (speedText == null)
            {
                return;
            }

            int roundedSpeed = Mathf.Max(0, Mathf.RoundToInt(speedKmh));
            string speedValue = padWithLeadingZeros
                ? roundedSpeed.ToString("000")
                : roundedSpeed.ToString();

            speedText.text = speedValue + " km/h";
        }

        private void EnsureUi()
        {
            if (canvasRoot == null && autoCreateCanvas)
            {
                CreateRuntimeCanvas();
            }

            if (canvasRoot != null && speedText == null)
            {
                speedText = canvasRoot.GetComponentInChildren<Text>(true);
            }

            if (speedText == null)
            {
                Debug.LogWarning($"SumoSpeedometerUI: speed text is missing on {name}. UI disabled.");
                _uiReady = false;
                return;
            }

            speedText.color = textColor;
            speedText.fontSize = Mathf.Max(12, fontSize);
            speedText.alignment = TextAnchor.MiddleCenter;
            speedText.enabled = true;

            if (canvasRoot != null)
            {
                canvasRoot.gameObject.SetActive(true);
            }

            _uiReady = true;
        }

        private void CreateRuntimeCanvas()
        {
            GameObject canvasObject = new GameObject($"{name}_SpeedometerCanvas");
            _runtimeCanvas = canvasObject.AddComponent<Canvas>();
            _runtimeCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _runtimeCanvas.sortingOrder = 220;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();

            GameObject textObject = new GameObject("SpeedText");
            textObject.transform.SetParent(canvasObject.transform, false);

            RectTransform textTransform = textObject.AddComponent<RectTransform>();
            textTransform.anchorMin = new Vector2(0.5f, 0f);
            textTransform.anchorMax = new Vector2(0.5f, 0f);
            textTransform.pivot = new Vector2(0.5f, 0f);
            textTransform.anchoredPosition = anchoredPosition;
            textTransform.sizeDelta = new Vector2(340f, 72f);

            Text textComponent = textObject.AddComponent<Text>();
            Font builtinFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (builtinFont == null)
            {
                builtinFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }

            textComponent.font = builtinFont;
            textComponent.fontStyle = FontStyle.Bold;
            textComponent.horizontalOverflow = HorizontalWrapMode.Overflow;
            textComponent.verticalOverflow = VerticalWrapMode.Overflow;
            textComponent.alignment = TextAnchor.MiddleCenter;
            textComponent.color = textColor;
            textComponent.fontSize = Mathf.Max(12, fontSize);

            Outline outline = textObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1.2f, -1.2f);

            canvasRoot = _runtimeCanvas;
            speedText = textComponent;
            _runtimeText = textComponent;
        }

        private void DisableProxyUi()
        {
            if (_runtimeCanvas != null)
            {
                _runtimeCanvas.gameObject.SetActive(false);
            }

            if (canvasRoot != null)
            {
                canvasRoot.gameObject.SetActive(false);
            }

            if (_runtimeText != null)
            {
                _runtimeText.enabled = false;
            }

            if (speedText != null)
            {
                speedText.enabled = false;
            }

            _uiReady = false;
        }

        private void OnValidate()
        {
            fontSize = Mathf.Max(12, fontSize);
            speedometerSmoothing = Mathf.Max(0.01f, speedometerSmoothing);
            kmhMultiplier = Mathf.Max(0.01f, kmhMultiplier);
        }
    }
}

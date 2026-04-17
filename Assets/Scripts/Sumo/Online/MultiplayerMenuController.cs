using UnityEngine;
using UnityEngine.UI;

namespace Sumo.Online
{
    [DisallowMultipleComponent]
    public sealed class MultiplayerMenuController : MonoBehaviour
    {
        [SerializeField] private MatchmakingClient matchmakingClient;
        [SerializeField] private MainMenuController mainMenuController;
        [SerializeField] private Canvas menuCanvas;

        [Header("UI")]
        [SerializeField] private Button findGameButton;
        [SerializeField] private Button cancelSearchButton;
        [SerializeField] private Button backButton;
        [SerializeField] private Text statusText;

        private bool _listenersBound;
        private bool _stateSubscribed;

        public void Configure(
            MatchmakingClient matchmakingClientReference,
            MainMenuController mainMenuControllerReference,
            Button findButton,
            Button cancelButton,
            Button backButtonReference,
            Text statusTextReference,
            Canvas menuCanvasReference)
        {
            UnbindButtonListeners();

            matchmakingClient = matchmakingClientReference;
            mainMenuController = mainMenuControllerReference;
            findGameButton = findButton;
            cancelSearchButton = cancelButton;
            backButton = backButtonReference;
            statusText = statusTextReference;
            menuCanvas = menuCanvasReference;

            BindButtonListeners();

            if (isActiveAndEnabled)
            {
                SubscribeState();
                if (matchmakingClient != null)
                {
                    OnStateChanged(matchmakingClient.CurrentState, matchmakingClient.CurrentStateText);
                }
            }
        }

        private void Awake()
        {
            BindButtonListeners();
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

        private void OnFindGamePressed()
        {
            if (matchmakingClient == null || matchmakingClient.IsSearching)
            {
                return;
            }

            matchmakingClient.FindGame();
        }

        private void OnCancelPressed()
        {
            if (matchmakingClient == null)
            {
                return;
            }

            matchmakingClient.CancelSearch();
        }

        private void OnBackPressed()
        {
            if (matchmakingClient != null && matchmakingClient.IsSearching)
            {
                matchmakingClient.CancelSearch();
            }

            if (mainMenuController != null)
            {
                mainMenuController.BackToMainMenu();
            }
        }

        private void OnStateChanged(MatchmakingClientState state, string status)
        {
            if (menuCanvas != null)
            {
                menuCanvas.enabled = state != MatchmakingClientState.Connected;
            }

            bool isSearching = state == MatchmakingClientState.Searching
                               || state == MatchmakingClientState.WaitingForPlayers
                               || state == MatchmakingClientState.MatchFound
                               || state == MatchmakingClientState.StartingServer
                               || state == MatchmakingClientState.Connecting;

            SetStatus(string.IsNullOrWhiteSpace(status) ? "Idle" : status);
            SetButtonsInteractable(isSearching);
        }

        private void SetStatus(string value)
        {
            if (statusText != null)
            {
                statusText.text = value;
            }
        }

        private void SetButtonsInteractable(bool isSearching)
        {
            if (findGameButton != null)
            {
                findGameButton.interactable = !isSearching;
            }

            if (cancelSearchButton != null)
            {
                cancelSearchButton.interactable = isSearching;
            }

            if (backButton != null)
            {
                backButton.interactable = true;
            }
        }

        private void BindButtonListeners()
        {
            if (_listenersBound)
            {
                return;
            }

            if (findGameButton != null)
            {
                findGameButton.onClick.AddListener(OnFindGamePressed);
            }

            if (cancelSearchButton != null)
            {
                cancelSearchButton.onClick.AddListener(OnCancelPressed);
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

            if (findGameButton != null)
            {
                findGameButton.onClick.RemoveListener(OnFindGamePressed);
            }

            if (cancelSearchButton != null)
            {
                cancelSearchButton.onClick.RemoveListener(OnCancelPressed);
            }

            if (backButton != null)
            {
                backButton.onClick.RemoveListener(OnBackPressed);
            }

            _listenersBound = false;
        }

        private void SubscribeState()
        {
            if (_stateSubscribed)
            {
                return;
            }

            if (matchmakingClient == null)
            {
                SetStatus("Failed");
                SetButtonsInteractable(isSearching: false);
                return;
            }

            matchmakingClient.StateChanged += OnStateChanged;
            _stateSubscribed = true;
            OnStateChanged(matchmakingClient.CurrentState, matchmakingClient.CurrentStateText);
        }

        private void UnsubscribeState()
        {
            if (!_stateSubscribed)
            {
                return;
            }

            if (matchmakingClient != null)
            {
                matchmakingClient.StateChanged -= OnStateChanged;
            }

            _stateSubscribed = false;
        }
    }
}

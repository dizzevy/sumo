using UnityEngine;

namespace Sumo.Online
{
    [DisallowMultipleComponent]
    public sealed class MainMenuController : MonoBehaviour
    {
        [SerializeField] private GameObject mainPanel;
        [SerializeField] private GameObject multiplayerPanel;
        [SerializeField] private GameObject settingsPanel;
        [SerializeField] private GameObject inGamePanel;

        private MenuPanel _settingsReturnPanel = MenuPanel.Main;
        private MenuPanel _currentPanel = MenuPanel.Main;

        public bool IsInGameMenuOpen => _currentPanel == MenuPanel.InGame;
        public bool IsSettingsOpenFromGame => _currentPanel == MenuPanel.Settings && _settingsReturnPanel == MenuPanel.InGame;

        public void Configure(
            GameObject mainPanelObject,
            GameObject multiplayerPanelObject,
            GameObject settingsPanelObject,
            GameObject inGamePanelObject = null)
        {
            mainPanel = mainPanelObject;
            multiplayerPanel = multiplayerPanelObject;
            settingsPanel = settingsPanelObject;
            inGamePanel = inGamePanelObject;
            ShowMainPanel();
        }

        private void Start()
        {
            ShowMainPanel();
        }

        public void OpenMultiplayer()
        {
            SetPanelState(MenuPanel.Multiplayer);
        }

        public void OpenSettings()
        {
            _settingsReturnPanel = MenuPanel.Main;
            SetPanelState(MenuPanel.Settings);
        }

        public void OpenSettingsFromInGame()
        {
            _settingsReturnPanel = MenuPanel.InGame;
            SetPanelState(MenuPanel.Settings);
        }

        public void BackToMainMenu()
        {
            ShowMainPanel();
        }

        public void BackFromSettings()
        {
            SetPanelState(_settingsReturnPanel == MenuPanel.InGame ? MenuPanel.InGame : MenuPanel.Main);
        }

        public void OpenInGameMenu()
        {
            SetPanelState(MenuPanel.InGame);
        }

        public void HideAllPanels()
        {
            SetPanelState(MenuPanel.None);
        }

        public void QuitGame()
        {
            Application.Quit();
        }

        private void ShowMainPanel()
        {
            SetPanelState(MenuPanel.Main);
        }

        private void SetPanelState(MenuPanel panel)
        {
            if (mainPanel != null)
            {
                mainPanel.SetActive(panel == MenuPanel.Main);
            }

            if (multiplayerPanel != null)
            {
                multiplayerPanel.SetActive(panel == MenuPanel.Multiplayer);
            }

            if (settingsPanel != null)
            {
                settingsPanel.SetActive(panel == MenuPanel.Settings);
            }

            if (inGamePanel != null)
            {
                inGamePanel.SetActive(panel == MenuPanel.InGame);
            }

            _currentPanel = panel;
        }

        private enum MenuPanel
        {
            None,
            Main,
            Multiplayer,
            Settings,
            InGame
        }
    }
}

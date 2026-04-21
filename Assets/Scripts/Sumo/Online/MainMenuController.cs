using UnityEngine;

namespace Sumo.Online
{
    [DisallowMultipleComponent]
    public sealed class MainMenuController : MonoBehaviour
    {
        [SerializeField] private GameObject mainPanel;
        [SerializeField] private GameObject multiplayerPanel;
        [SerializeField] private GameObject settingsPanel;

        public void Configure(GameObject mainPanelObject, GameObject multiplayerPanelObject, GameObject settingsPanelObject)
        {
            mainPanel = mainPanelObject;
            multiplayerPanel = multiplayerPanelObject;
            settingsPanel = settingsPanelObject;
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
            SetPanelState(MenuPanel.Settings);
        }

        public void BackToMainMenu()
        {
            ShowMainPanel();
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
        }

        private enum MenuPanel
        {
            Main,
            Multiplayer,
            Settings
        }
    }
}

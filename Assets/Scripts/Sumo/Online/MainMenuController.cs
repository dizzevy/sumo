using UnityEngine;

namespace Sumo.Online
{
    [DisallowMultipleComponent]
    public sealed class MainMenuController : MonoBehaviour
    {
        [SerializeField] private GameObject mainPanel;
        [SerializeField] private GameObject multiplayerPanel;

        public void Configure(GameObject mainPanelObject, GameObject multiplayerPanelObject)
        {
            mainPanel = mainPanelObject;
            multiplayerPanel = multiplayerPanelObject;
            ShowMainPanel();
        }

        private void Start()
        {
            ShowMainPanel();
        }

        public void OpenMultiplayer()
        {
            SetPanelState(showMainPanel: false);
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
            SetPanelState(showMainPanel: true);
        }

        private void SetPanelState(bool showMainPanel)
        {
            if (mainPanel != null)
            {
                mainPanel.SetActive(showMainPanel);
            }

            if (multiplayerPanel != null)
            {
                multiplayerPanel.SetActive(!showMainPanel);
            }
        }
    }
}

using UnityEngine;

namespace Sumo.Online
{
    [CreateAssetMenu(fileName = "BootstrapConfig", menuName = "Sumo/Bootstrap Config")]
    public sealed class BootstrapConfig : ScriptableObject
    {
        public const int TargetMaxPlayers = 10;

        [Header("Match Defaults")]
        [SerializeField] private string defaultSceneName = "location_test";
        [SerializeField] private int defaultMaxPlayers = TargetMaxPlayers;
        [SerializeField] private int minimumPlayersToStart = 2;
        [SerializeField] private ushort defaultServerPort = 27015;

        [Header("Mock Matchmaking")]
        [SerializeField] private bool useMockMatchmakingInEditor = true;
        [SerializeField] private string mockSessionName = "sumo_match_001";
        [SerializeField] private string mockRegion = "local";
        [SerializeField] private string mockMatchId = "match_001";
        [SerializeField] private float mockSearchDelaySeconds = 0.8f;
        [SerializeField] private float mockWaitForPlayersDelaySeconds = 2.0f;
        [SerializeField] private float mockServerBootDelaySeconds = 1.5f;

        [Header("Production Matchmaking")]
        [SerializeField] private string productionBackendBaseUrl = "https://your-backend.example.com";
        [SerializeField] private float productionPollIntervalSeconds = 1.0f;
        [SerializeField] private string playerIdPrefix = "player";

        public string DefaultSceneName => defaultSceneName;
        public int DefaultMaxPlayers => Mathf.Clamp(defaultMaxPlayers, 2, TargetMaxPlayers);
        public int MinimumPlayersToStart => Mathf.Clamp(minimumPlayersToStart, 2, TargetMaxPlayers);
        public ushort DefaultServerPort => defaultServerPort;

        public bool UseMockMatchmakingInEditor => useMockMatchmakingInEditor;
        public string MockSessionName => mockSessionName;
        public string MockRegion => mockRegion;
        public string MockMatchId => mockMatchId;
        public float MockSearchDelaySeconds => Mathf.Max(0f, mockSearchDelaySeconds);
        public float MockWaitForPlayersDelaySeconds => Mathf.Max(0f, mockWaitForPlayersDelaySeconds);
        public float MockServerBootDelaySeconds => Mathf.Max(0f, mockServerBootDelaySeconds);

        public string ProductionBackendBaseUrl => productionBackendBaseUrl;
        public float ProductionPollIntervalSeconds => Mathf.Max(0.2f, productionPollIntervalSeconds);
        public string PlayerIdPrefix => string.IsNullOrWhiteSpace(playerIdPrefix) ? "player" : playerIdPrefix;

        private void OnValidate()
        {
            defaultMaxPlayers = Mathf.Clamp(defaultMaxPlayers, 2, TargetMaxPlayers);
            minimumPlayersToStart = Mathf.Clamp(minimumPlayersToStart, 2, TargetMaxPlayers);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Fusion;
using Fusion.Addons.Physics;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Sumo.Online
{
    [DisallowMultipleComponent]
    public sealed class DedicatedServerBootstrap : MonoBehaviour
    {
        private const int HardMaxPlayers = BootstrapConfig.TargetMaxPlayers;

        [Header("References")]
        [SerializeField] private BootstrapConfig bootstrapConfig;
        [SerializeField] private NetworkPrefabRef playerPrefab;

        [Header("Behavior")]
        [SerializeField] private bool autoStartOnlyInBatchMode = true;
        [SerializeField] private bool allowServerStartInEditor;
        [SerializeField] private bool shutdownWhenPlayersDropBelowMinimum;

        private NetworkRunner _runner;

        private async void Start()
        {
            if (!ShouldRunAsDedicatedServer())
            {
                return;
            }

            if (!playerPrefab.IsValid)
            {
                Debug.LogError("DedicatedServerBootstrap: player prefab is not assigned. Assign Assets/Prefabs/Player.prefab in Network Prefab Table and this component.");
                return;
            }

            LaunchParameters launchParameters = ReadLaunchParameters();
            await StartDedicatedServerAsync(launchParameters);
        }

        private bool ShouldRunAsDedicatedServer()
        {
#if UNITY_SERVER
            return true;
#else
            bool hasServerArg = HasArg("server") || HasArg("dedicatedServer");

            if (Application.isBatchMode)
            {
                return true;
            }

            if (autoStartOnlyInBatchMode)
            {
                return allowServerStartInEditor && hasServerArg;
            }

            return hasServerArg;
#endif
        }

        private async Task StartDedicatedServerAsync(LaunchParameters launch)
        {
            GameObject runnerObject = new GameObject("DedicatedServerRunner");
            DontDestroyOnLoad(runnerObject);

            _runner = runnerObject.AddComponent<NetworkRunner>();
            _runner.ProvideInput = false;

            NetworkSceneManagerDefault sceneManager = runnerObject.AddComponent<NetworkSceneManagerDefault>();

            RunnerSimulatePhysics3D physicsSimulator = runnerObject.GetComponent<RunnerSimulatePhysics3D>();
            if (physicsSimulator == null)
            {
                physicsSimulator = runnerObject.AddComponent<RunnerSimulatePhysics3D>();
            }

            physicsSimulator.ClientPhysicsSimulation = ClientPhysicsSimulation.Disabled;

            DedicatedServerMatchController matchController = runnerObject.AddComponent<DedicatedServerMatchController>();
            matchController.Initialize(
                _runner,
                playerPrefab,
                launch.MinimumPlayers,
                shutdownWhenPlayersDropBelowMinimum);

            Dictionary<string, SessionProperty> sessionProperties = new Dictionary<string, SessionProperty>
            {
                ["mode"] = "sumo",
                ["map"] = launch.SceneName,
                ["matchId"] = launch.MatchId,
                ["minPlayers"] = launch.MinimumPlayers,
                ["region"] = launch.Region
            };

            StartGameArgs startArgs = new StartGameArgs
            {
                GameMode = GameMode.Server,
                SessionName = launch.SessionName,
                PlayerCount = launch.MaxPlayers,
                Address = NetAddress.Any(launch.Port),
                SceneManager = sceneManager,
                SessionProperties = sessionProperties,
                IsOpen = true,
                IsVisible = false
            };

            StartGameResult result = await _runner.StartGame(startArgs);
            if (!result.Ok)
            {
                Debug.LogError($"DedicatedServerBootstrap: failed to start server. Reason={result.ShutdownReason}; Error={result.ErrorMessage}");
                return;
            }

            if (_runner.IsSceneAuthority)
            {
                if (!TryResolveSceneRef(launch, out SceneRef sceneRef))
                {
                    Debug.LogError($"DedicatedServerBootstrap: scene '{launch.SceneName}' is not in Build Settings. Use -sceneIndex or add scene to build list.");
                    return;
                }

                await _runner.LoadScene(sceneRef, LoadSceneMode.Single, LocalPhysicsMode.None, true);
            }

            Debug.Log($"Dedicated server started. Session={launch.SessionName}, Match={launch.MatchId}, Region={launch.Region}, MaxPlayers={launch.MaxPlayers}, Port={launch.Port}");
        }

        private LaunchParameters ReadLaunchParameters()
        {
            string fallbackSessionName = bootstrapConfig != null ? bootstrapConfig.MockSessionName : "sumo_match_001";
            string fallbackMatchId = bootstrapConfig != null ? bootstrapConfig.MockMatchId : "match_001";
            string fallbackRegion = "auto";
            string fallbackScene = bootstrapConfig != null ? bootstrapConfig.DefaultSceneName : "Location1";
            int fallbackMaxPlayers = bootstrapConfig != null ? bootstrapConfig.DefaultMaxPlayers : HardMaxPlayers;
            int fallbackMinPlayers = bootstrapConfig != null ? bootstrapConfig.MinimumPlayersToStart : 2;
            ushort fallbackPort = bootstrapConfig != null ? bootstrapConfig.DefaultServerPort : (ushort)27015;
            int requestedMaxPlayers = GetIntArgValue("maxPlayers", fallbackMaxPlayers).GetValueOrDefault(fallbackMaxPlayers);

            // Dedicated server currently runs at a fixed 10-player cap.
            int resolvedMaxPlayers = Mathf.Clamp(Mathf.Max(HardMaxPlayers, requestedMaxPlayers), 2, HardMaxPlayers);

            LaunchParameters launch = new LaunchParameters
            {
                SessionName = GetArgValue("sessionName", fallbackSessionName),
                MatchId = GetArgValue("matchId", fallbackMatchId),
                Region = GetArgValue("region", fallbackRegion),
                SceneName = GetArgValue("scene", fallbackScene),
                SceneIndex = GetIntArgValue("sceneIndex", null),
                MaxPlayers = resolvedMaxPlayers,
                MinimumPlayers = Mathf.Clamp(
                    GetIntArgValue("minPlayers", fallbackMinPlayers).GetValueOrDefault(fallbackMinPlayers),
                    2,
                    resolvedMaxPlayers),
                Port = (ushort)Mathf.Clamp(GetIntArgValue("port", fallbackPort).GetValueOrDefault(fallbackPort), 0, ushort.MaxValue)
            };

            if (string.IsNullOrWhiteSpace(launch.SceneName))
            {
                launch.SceneName = "Location1";
            }

            return launch;
        }

        private static bool TryResolveSceneRef(LaunchParameters launch, out SceneRef sceneRef)
        {
            sceneRef = default;

            if (launch.SceneIndex.HasValue)
            {
                int sceneCount = SceneManager.sceneCountInBuildSettings;
                int index = launch.SceneIndex.Value;
                if (index >= 0 && index < sceneCount)
                {
                    sceneRef = SceneRef.FromIndex(index);
                    return true;
                }
            }

            int resolvedIndex = ResolveSceneBuildIndex(launch.SceneName);
            if (resolvedIndex >= 0)
            {
                sceneRef = SceneRef.FromIndex(resolvedIndex);
                return true;
            }

            return false;
        }

        private static int ResolveSceneBuildIndex(string sceneIdentifier)
        {
            if (string.IsNullOrWhiteSpace(sceneIdentifier))
            {
                return -1;
            }

            string normalizedInput = sceneIdentifier.Replace('\\', '/');
            string inputName = Path.GetFileNameWithoutExtension(normalizedInput);
            string inputPath = normalizedInput.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                ? normalizedInput
                : normalizedInput + ".unity";

            int sceneCount = SceneManager.sceneCountInBuildSettings;
            for (int index = 0; index < sceneCount; index++)
            {
                string path = SceneUtility.GetScenePathByBuildIndex(index);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string normalizedPath = path.Replace('\\', '/');
                string sceneName = Path.GetFileNameWithoutExtension(normalizedPath);

                if (string.Equals(normalizedPath, normalizedInput, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedPath, inputPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(sceneName, normalizedInput, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(sceneName, inputName, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        private bool HasArg(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            string shortKey = "-" + key;
            string longKey = "--" + key;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], shortKey, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], longKey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private string GetArgValue(string key, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            string shortKey = "-" + key;
            string longKey = "--" + key;

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], shortKey, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(args[i], longKey, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return fallback;
        }

        private int? GetIntArgValue(string key, int? fallback)
        {
            string raw = GetArgValue(key, null);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (int.TryParse(raw, out int value))
            {
                return value;
            }

            return fallback;
        }

        private struct LaunchParameters
        {
            public string SessionName;
            public string MatchId;
            public string Region;
            public string SceneName;
            public int? SceneIndex;
            public int MaxPlayers;
            public int MinimumPlayers;
            public ushort Port;
        }
    }
}

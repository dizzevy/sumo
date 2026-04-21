using Fusion;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sumo.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class PlayerRoundState : NetworkBehaviour
    {
        [Header("Gameplay Components")]
        [SerializeField] private Sumo.SumoBallController ballController;
        [SerializeField] private Sumo.SumoCollisionController collisionController;
        [SerializeField] private Sumo.SumoPlayerInput playerInput;
        [SerializeField] private Sumo.SumoCameraFollow cameraFollow;
        [SerializeField] private Rigidbody body;
        [SerializeField] private Collider[] gameplayColliders;
        [SerializeField] private Renderer[] gameplayRenderers;

        [Header("Death VFX")]
        [SerializeField] private bool enableDeathVfx = true;
        [SerializeField] private int deathVfxShardCount = 24;
        [SerializeField] private float deathVfxLifetime = 2.2f;
        [SerializeField] private float deathVfxSpawnYOffset = 0.1f;
        [SerializeField] private Color deathVfxPrimaryColor = new Color(1f, 0.18f, 0.08f, 1f);
        [SerializeField] private Color deathVfxHighlightColor = new Color(1f, 0.62f, 0.12f, 1f);
        [SerializeField] private float deathVfxIntensity = 1.55f;
        [SerializeField] private float deathToSpectatorDelay = -1f;

        [Networked] public NetworkBool IsClientReady { get; private set; }
        [Networked] public NetworkBool IsAlive { get; private set; }
        [Networked] public NetworkBool IsSpectator { get; private set; }
        [Networked] public int LastRoundIndex { get; private set; }
        [Networked] public int EliminationOrder { get; private set; }
        [Networked] private NetworkBool PendingSpectatorTransition { get; set; }
        [Networked] private TickTimer PendingSpectatorTransitionTimer { get; set; }
        [Networked] private Vector3 PendingSpectatorPosition { get; set; }
        [Networked] private Vector3 PendingSpectatorEulerAngles { get; set; }

        public bool IsAliveInRound => IsAlive && !IsSpectator;

        public Vector3 ZoneCheckPosition
        {
            get
            {
                if (body != null)
                {
                    return body.worldCenterOfMass;
                }

                return transform.position;
            }
        }

        private bool _stateApplied;
        private NetworkBool _lastAppliedAlive;
        private NetworkBool _lastAppliedSpectator;
        private bool[] _gameplayRendererDefaultEnabled;

        public override void Spawned()
        {
            CacheComponentsIfNeeded();

            if (HasStateAuthority)
            {
                IsAlive = false;
                IsSpectator = true;
                EliminationOrder = 0;
                PendingSpectatorTransition = false;
                PendingSpectatorTransitionTimer = default;
                PendingSpectatorPosition = Vector3.zero;
                PendingSpectatorEulerAngles = Vector3.zero;
                LastRoundIndex = 0;
                IsClientReady = false;
            }

            if (Object != null && Object.HasInputAuthority)
            {
                RPC_ReportClientReady();
                EnsureLocalHudController();
            }

            ApplyState(force: true);
        }

        public override void Render()
        {
            ApplyState(force: false);
        }

        public override void FixedUpdateNetwork()
        {
            if (!HasStateAuthority || !PendingSpectatorTransition)
            {
                return;
            }

            if (!PendingSpectatorTransitionTimer.ExpiredOrNotRunning(Runner))
            {
                return;
            }

            FinalizePendingSpectatorTransition();
        }

        public void ServerPrepareForRound(int roundIndex, Vector3 position, Quaternion rotation)
        {
            if (!HasStateAuthority)
            {
                return;
            }

            LastRoundIndex = Mathf.Max(0, roundIndex);
            EliminationOrder = 0;
            IsAlive = true;
            IsSpectator = false;
            PendingSpectatorTransition = false;
            PendingSpectatorTransitionTimer = default;
            PendingSpectatorPosition = Vector3.zero;
            PendingSpectatorEulerAngles = Vector3.zero;
            TeleportServer(position, rotation);
            ApplyState(force: true);
        }

        public void ServerEliminateToSpectator(int eliminationOrder, Vector3 spectatorPosition, Quaternion spectatorRotation)
        {
            if (!HasStateAuthority)
            {
                return;
            }

            if (enableDeathVfx)
            {
                Vector3 deathPosition = ZoneCheckPosition + Vector3.up * deathVfxSpawnYOffset;
                Color tint = ResolveDeathTint();
                Color highlightTint = ResolveDeathHighlightTint();
                RPC_PlayDeathVfx(
                    deathPosition,
                    new Vector3(tint.r, tint.g, tint.b),
                    new Vector3(highlightTint.r, highlightTint.g, highlightTint.b),
                    deathVfxShardCount,
                    deathVfxLifetime,
                    deathVfxIntensity);
            }

            EliminationOrder = Mathf.Max(1, eliminationOrder);
            IsAlive = false;
            float transitionDelaySeconds = ResolveDeathToSpectatorDelay();
            if (transitionDelaySeconds <= 0.001f)
            {
                IsSpectator = true;
                PendingSpectatorTransition = false;
                PendingSpectatorTransitionTimer = default;
                PendingSpectatorPosition = Vector3.zero;
                PendingSpectatorEulerAngles = Vector3.zero;
                TeleportServer(spectatorPosition, spectatorRotation);
            }
            else
            {
                IsSpectator = false;
                PendingSpectatorTransition = true;
                PendingSpectatorTransitionTimer = TickTimer.CreateFromSeconds(Runner, transitionDelaySeconds);
                PendingSpectatorPosition = spectatorPosition;
                PendingSpectatorEulerAngles = spectatorRotation.eulerAngles;
            }

            ApplyState(force: true);
        }

        public void ServerForceClientReady(bool value)
        {
            if (!HasStateAuthority)
            {
                return;
            }

            IsClientReady = value;
        }

        [Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority, Channel = RpcChannel.Reliable)]
        private void RPC_ReportClientReady()
        {
            IsClientReady = true;
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All, Channel = RpcChannel.Reliable)]
        private void RPC_PlayDeathVfx(Vector3 position, Vector3 tintRgb, Vector3 highlightRgb, int shardCount, float lifetime, float intensity)
        {
            if (Application.isBatchMode && Runner != null && Runner.IsServer && !Runner.IsClient)
            {
                return;
            }

            Color tint = new Color(
                Mathf.Clamp01(tintRgb.x),
                Mathf.Clamp01(tintRgb.y),
                Mathf.Clamp01(tintRgb.z),
                1f);

            Color highlightTint = new Color(
                Mathf.Clamp01(highlightRgb.x),
                Mathf.Clamp01(highlightRgb.y),
                Mathf.Clamp01(highlightRgb.z),
                1f);

            PlayerDeathVfx.Spawn(position, tint, highlightTint, shardCount, lifetime, intensity);
        }

        private void TeleportServer(Vector3 position, Quaternion rotation)
        {
            transform.SetPositionAndRotation(position, rotation);

            if (body == null)
            {
                return;
            }

            body.position = position;
            body.rotation = rotation;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        private void ApplyState(bool force)
        {
            if (!force && _stateApplied && _lastAppliedAlive == IsAlive && _lastAppliedSpectator == IsSpectator)
            {
                return;
            }

            CacheComponentsIfNeeded();

            bool gameplayEnabled = IsAlive && !IsSpectator;
            bool keepLocalDeathCamera = !IsAlive && !IsSpectator && PendingSpectatorTransition;
            bool localPlayer = Object != null && Object.HasInputAuthority;

            if (ballController != null)
            {
                ballController.enabled = gameplayEnabled;
            }

            if (collisionController != null)
            {
                collisionController.enabled = gameplayEnabled;
            }

            if (playerInput != null)
            {
                if (localPlayer)
                {
                    playerInput.enabled = gameplayEnabled;
                }
                else
                {
                    playerInput.enabled = false;
                }
            }

            if (cameraFollow != null && localPlayer)
            {
                cameraFollow.enabled = gameplayEnabled || keepLocalDeathCamera;
            }

            if (gameplayColliders != null)
            {
                for (int i = 0; i < gameplayColliders.Length; i++)
                {
                    Collider col = gameplayColliders[i];
                    if (col == null || col.isTrigger)
                    {
                        continue;
                    }

                    col.enabled = gameplayEnabled;
                }
            }

            bool showGameplayVisuals = IsAlive;
            if (gameplayRenderers != null)
            {
                for (int i = 0; i < gameplayRenderers.Length; i++)
                {
                    Renderer renderer = gameplayRenderers[i];
                    if (renderer == null)
                    {
                        continue;
                    }

                    bool defaultEnabled = _gameplayRendererDefaultEnabled != null
                                          && i < _gameplayRendererDefaultEnabled.Length
                                          && _gameplayRendererDefaultEnabled[i];
                    renderer.enabled = showGameplayVisuals && defaultEnabled;
                }
            }

            if (body != null)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = !gameplayEnabled;
                body.detectCollisions = gameplayEnabled;
            }

            _lastAppliedAlive = IsAlive;
            _lastAppliedSpectator = IsSpectator;
            _stateApplied = true;
        }

        private void FinalizePendingSpectatorTransition()
        {
            if (!HasStateAuthority || !PendingSpectatorTransition)
            {
                return;
            }

            Vector3 spectatorPosition = PendingSpectatorPosition;
            Quaternion spectatorRotation = Quaternion.Euler(PendingSpectatorEulerAngles);

            PendingSpectatorTransition = false;
            PendingSpectatorTransitionTimer = default;
            PendingSpectatorPosition = Vector3.zero;
            PendingSpectatorEulerAngles = Vector3.zero;

            IsSpectator = true;
            TeleportServer(spectatorPosition, spectatorRotation);
            ApplyState(force: true);
        }

        private void CacheComponentsIfNeeded()
        {
            if (ballController == null)
            {
                ballController = GetComponent<Sumo.SumoBallController>();
            }

            if (collisionController == null)
            {
                collisionController = GetComponent<Sumo.SumoCollisionController>();
            }

            if (playerInput == null)
            {
                playerInput = GetComponent<Sumo.SumoPlayerInput>();
            }

            if (cameraFollow == null)
            {
                cameraFollow = GetComponent<Sumo.SumoCameraFollow>();
            }

            if (body == null)
            {
                body = GetComponent<Rigidbody>();
            }

            if (gameplayColliders == null || gameplayColliders.Length == 0)
            {
                gameplayColliders = GetComponentsInChildren<Collider>(includeInactive: true);
            }

            if (gameplayRenderers == null || gameplayRenderers.Length == 0)
            {
                gameplayRenderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            }

            if (gameplayRenderers != null && gameplayRenderers.Length > 0)
            {
                if (_gameplayRendererDefaultEnabled == null || _gameplayRendererDefaultEnabled.Length != gameplayRenderers.Length)
                {
                    _gameplayRendererDefaultEnabled = new bool[gameplayRenderers.Length];
                    for (int i = 0; i < gameplayRenderers.Length; i++)
                    {
                        Renderer renderer = gameplayRenderers[i];
                        _gameplayRendererDefaultEnabled[i] = renderer != null && renderer.enabled;
                    }
                }
            }
        }

        private void OnValidate()
        {
            CacheComponentsIfNeeded();
            deathVfxShardCount = Mathf.Clamp(deathVfxShardCount, 6, 96);
            deathVfxLifetime = Mathf.Max(0.35f, deathVfxLifetime);
            deathVfxSpawnYOffset = Mathf.Clamp(deathVfxSpawnYOffset, -1f, 1f);
            deathVfxPrimaryColor = new Color(
                Mathf.Clamp01(deathVfxPrimaryColor.r),
                Mathf.Clamp01(deathVfxPrimaryColor.g),
                Mathf.Clamp01(deathVfxPrimaryColor.b),
                1f);
            deathVfxHighlightColor = new Color(
                Mathf.Clamp01(deathVfxHighlightColor.r),
                Mathf.Clamp01(deathVfxHighlightColor.g),
                Mathf.Clamp01(deathVfxHighlightColor.b),
                1f);
            deathVfxIntensity = Mathf.Clamp(deathVfxIntensity, 1f, 3f);
            if (deathToSpectatorDelay < 0f)
            {
                deathToSpectatorDelay = -1f;
            }
            else
            {
                deathToSpectatorDelay = Mathf.Max(0f, deathToSpectatorDelay);
            }
        }

        private void EnsureLocalHudController()
        {
            MatchHudController existing = GetComponent<MatchHudController>();
            if (existing == null)
            {
                gameObject.AddComponent<MatchHudController>();
            }
        }

        private Color ResolveDeathTint()
        {
            return deathVfxPrimaryColor;
        }

        private Color ResolveDeathHighlightTint()
        {
            return deathVfxHighlightColor;
        }

        private float ResolveDeathToSpectatorDelay()
        {
            if (deathToSpectatorDelay >= 0f)
            {
                return Mathf.Max(0f, deathToSpectatorDelay);
            }

            return enableDeathVfx ? Mathf.Max(0f, deathVfxLifetime) : 0f;
        }
    }

    internal static class PlayerDeathVfx
    {
        private static Mesh _sphereMesh;

        public static void Spawn(Vector3 position, Color tint, Color highlightTint, int shardCount, float lifetimeSeconds, float intensity)
        {
            float intensityScale = Mathf.Clamp(intensity, 1f, 3f);
            float intensity01 = (intensityScale - 1f) / 2f;
            float lifetime = Mathf.Clamp(lifetimeSeconds * Mathf.Lerp(1f, 1.3f, intensity01), 0.35f, 9f);
            int shards = Mathf.Clamp(Mathf.RoundToInt(shardCount * Mathf.Lerp(1.2f, 1.9f, intensity01)), 8, 128);

            GameObject root = new GameObject("PlayerDeathVfx");
            root.transform.position = position;

            TimedDestroy timedDestroy = root.AddComponent<TimedDestroy>();
            timedDestroy.Lifetime = lifetime;

            SpawnFlashLight(root.transform, tint, highlightTint, lifetime, intensityScale);
            SpawnBurstParticles(root.transform, tint, highlightTint, lifetime, intensityScale);
            SpawnSparkSpray(root.transform, tint, highlightTint, lifetime, intensityScale);
            SpawnShockwave(root.transform, tint, highlightTint, lifetime, intensityScale);
            SpawnShards(root.transform, tint, highlightTint, shards, lifetime, intensityScale);
        }

        private static void SpawnBurstParticles(Transform parent, Color tint, Color highlightTint, float lifetime, float intensity)
        {
            GameObject particlesObject = new GameObject("BurstParticles");
            particlesObject.transform.SetParent(parent, false);

            ParticleSystem ps = particlesObject.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.loop = false;
            main.playOnAwake = true;
            main.duration = Mathf.Min(1.35f, lifetime);
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.28f, 0.72f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(6.5f * intensity, 15.8f * intensity);
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.23f);
            main.maxParticles = 260;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(highlightTint.r, highlightTint.g, highlightTint.b, 1f),
                new Color(tint.r, tint.g, tint.b, 0.78f));

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            short firstBurstMin = (short)Mathf.RoundToInt(48f * intensity);
            short firstBurstMax = (short)Mathf.RoundToInt(76f * intensity);
            short secondBurstMin = (short)Mathf.RoundToInt(20f * intensity);
            short secondBurstMax = (short)Mathf.RoundToInt(34f * intensity);
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, firstBurstMin, firstBurstMax, 1, 0f),
                new ParticleSystem.Burst(0.07f, secondBurstMin, secondBurstMax, 1, 0f)
            });

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.26f;

            ParticleSystem.ColorOverLifetimeModule colorLifetime = ps.colorOverLifetime;
            colorLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.Lerp(highlightTint, Color.white, 0.28f), 0f),
                    new GradientColorKey(highlightTint, 0.42f),
                    new GradientColorKey(tint, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.85f, 0.35f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorLifetime.color = gradient;

            ParticleSystem.SizeOverLifetimeModule sizeLifetime = ps.sizeOverLifetime;
            sizeLifetime.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.55f, 0.68f),
                new Keyframe(1f, 0f));
            sizeLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            renderer.sharedMaterial = CreateParticleMaterial(tint);

            ps.Play(true);
        }

        private static void SpawnSparkSpray(Transform parent, Color tint, Color highlightTint, float lifetime, float intensity)
        {
            GameObject sparksObject = new GameObject("SparkSpray");
            sparksObject.transform.SetParent(parent, false);

            ParticleSystem ps = sparksObject.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.loop = false;
            main.playOnAwake = true;
            main.duration = Mathf.Min(1.6f, lifetime);
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.35f, 0.95f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(9f * intensity, 22f * intensity);
            main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.08f);
            main.maxParticles = 180;
            main.gravityModifier = 0.65f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(highlightTint.r, highlightTint.g, highlightTint.b, 1f),
                new Color(tint.r, tint.g, tint.b, 0.9f));

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            short burstMin = (short)Mathf.RoundToInt(34f * intensity);
            short burstMax = (short)Mathf.RoundToInt(62f * intensity);
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0.02f, burstMin, burstMax, 1, 0f)
            });

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 28f;
            shape.radius = 0.08f;
            shape.arcMode = ParticleSystemShapeMultiModeValue.Random;

            ParticleSystem.ColorOverLifetimeModule colorLifetime = ps.colorOverLifetime;
            colorLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(highlightTint, 0f),
                    new GradientColorKey(new Color(1f, 0.35f, 0.14f, 1f), 0.55f),
                    new GradientColorKey(new Color(0.65f, 0.09f, 0.04f, 1f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.72f, 0.45f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorLifetime.color = gradient;

            ParticleSystem.TrailModule trails = ps.trails;
            trails.enabled = true;
            trails.mode = ParticleSystemTrailMode.PerParticle;
            trails.lifetime = 0.16f;
            trails.ratio = 1f;
            trails.dieWithParticles = true;
            trails.minVertexDistance = 0.04f;
            trails.widthOverTrail = new ParticleSystem.MinMaxCurve(0.9f);

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.velocityScale = 0.25f;
            renderer.lengthScale = 1.8f;
            renderer.sharedMaterial = CreateParticleMaterial(highlightTint);

            ps.Play(true);
        }

        private static void SpawnShockwave(Transform parent, Color tint, Color highlightTint, float lifetime, float intensity)
        {
            GameObject waveObject = new GameObject("Shockwave");
            waveObject.transform.SetParent(parent, false);

            ParticleSystem ps = waveObject.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.loop = false;
            main.playOnAwake = true;
            main.duration = Mathf.Min(0.75f, lifetime);
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.42f, 0.65f);
            main.startSpeed = 0f;
            main.startSize = 0.36f;
            main.maxParticles = 6;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(highlightTint.r, highlightTint.g, highlightTint.b, 0.72f),
                new Color(tint.r, tint.g, tint.b, 0.6f));

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, 1, 2, 1, 0f)
            });

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.02f;

            ParticleSystem.SizeOverLifetimeModule sizeLifetime = ps.sizeOverLifetime;
            sizeLifetime.enabled = true;
            AnimationCurve sizeCurve = new AnimationCurve(
                new Keyframe(0f, 0.2f),
                new Keyframe(0.28f, 1.7f * intensity),
                new Keyframe(1f, 3.9f * intensity));
            sizeLifetime.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

            ParticleSystem.ColorOverLifetimeModule colorLifetime = ps.colorOverLifetime;
            colorLifetime.enabled = true;
            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(highlightTint, 0f),
                    new GradientColorKey(tint, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0.95f, 0f),
                    new GradientAlphaKey(0.42f, 0.38f),
                    new GradientAlphaKey(0f, 1f)
                });
            colorLifetime.color = gradient;

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = CreateParticleMaterial(highlightTint);

            ps.Play(true);
        }

        private static void SpawnShards(Transform parent, Color tint, Color highlightTint, int shardCount, float lifetime, float intensity)
        {
            Material shardMaterial = CreateShardMaterial(Color.Lerp(tint, highlightTint, 0.34f));
            Mesh shardMesh = GetSphereMesh();

            for (int i = 0; i < shardCount; i++)
            {
                GameObject shard = new GameObject("Shard");
                shard.transform.SetParent(parent, false);

                float size = Random.Range(0.055f, 0.14f);
                Vector3 randomDirection = Random.onUnitSphere;
                if (randomDirection.sqrMagnitude < 0.001f)
                {
                    randomDirection = Vector3.up;
                }

                shard.transform.localPosition = randomDirection * Random.Range(0.04f, 0.22f);
                shard.transform.localRotation = Random.rotationUniform;
                shard.transform.localScale = Vector3.one * size;

                MeshFilter meshFilter = shard.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = shardMesh;

                MeshRenderer meshRenderer = shard.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterial = shardMaterial;

                SphereCollider collider = shard.AddComponent<SphereCollider>();
                collider.radius = 0.5f;

                Rigidbody rb = shard.AddComponent<Rigidbody>();
                rb.useGravity = true;
                rb.mass = 0.05f;
                rb.linearDamping = 0.35f;
                rb.angularDamping = 0.2f;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                float outwardSpeed = Random.Range(6.6f, 14.8f) * intensity;
                Vector3 burstDirection = (randomDirection + Vector3.up * 0.28f).normalized;
                rb.linearVelocity = burstDirection * outwardSpeed + Vector3.up * Random.Range(0.8f, 2.4f);
                rb.angularVelocity = Random.onUnitSphere * Random.Range(11f, 28f);

                Object.Destroy(shard, lifetime);
            }
        }

        private static void SpawnFlashLight(Transform parent, Color tint, Color highlightTint, float lifetime, float intensity)
        {
            GameObject lightObject = new GameObject("DeathFlashLight");
            lightObject.transform.SetParent(parent, false);
            lightObject.transform.localPosition = Vector3.up * 0.15f;

            Light flashLight = lightObject.AddComponent<Light>();
            flashLight.type = LightType.Point;
            flashLight.range = Mathf.Lerp(6.5f, 12.5f, (intensity - 1f) / 2f);
            flashLight.intensity = Mathf.Lerp(7f, 15f, (intensity - 1f) / 2f);
            flashLight.color = Color.Lerp(tint, highlightTint, 0.55f);
            flashLight.shadows = LightShadows.None;

            LightPulse pulse = lightObject.AddComponent<LightPulse>();
            pulse.Target = flashLight;
            pulse.Duration = Mathf.Min(0.26f, lifetime * 0.35f);
        }

        private static Material CreateShardMaterial(Color tint)
        {
            Shader shader = ChooseSurfaceShader();
            Material material = new Material(shader)
            {
                name = "DeathShardMat",
                color = tint
            };

            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", tint * 1.6f);
            }

            return material;
        }

        private static Material CreateParticleMaterial(Color tint)
        {
            Shader shader = FindFirstSupportedShader(
                "Universal Render Pipeline/Particles/Unlit",
                "Particles/Standard Unlit",
                "Sprites/Default",
                "Unlit/Color",
                "Standard");

            Material material = new Material(shader)
            {
                name = "DeathParticleMat",
                color = tint
            };

            ConfigureTransparent(material);
            return material;
        }

        private static Shader ChooseSurfaceShader()
        {
            bool hasRenderPipeline = GraphicsSettings.currentRenderPipeline != null;
            return hasRenderPipeline
                ? FindFirstSupportedShader(
                    "Universal Render Pipeline/Lit",
                    "Universal Render Pipeline/Simple Lit",
                    "Universal Render Pipeline/Unlit",
                    "Standard")
                : FindFirstSupportedShader(
                    "Standard",
                    "Legacy Shaders/Diffuse",
                    "Unlit/Color");
        }

        private static Shader FindFirstSupportedShader(params string[] shaderNames)
        {
            if (shaderNames != null)
            {
                for (int i = 0; i < shaderNames.Length; i++)
                {
                    string name = shaderNames[i];
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    Shader shader = Shader.Find(name);
                    if (shader != null && shader.isSupported)
                    {
                        return shader;
                    }
                }
            }

            Shader fallback = Shader.Find("Standard");
            if (fallback != null)
            {
                return fallback;
            }

            return Shader.Find("Hidden/InternalErrorShader");
        }

        private static void ConfigureTransparent(Material material)
        {
            if (material == null)
            {
                return;
            }

            if (material.HasProperty("_Surface"))
            {
                material.SetFloat("_Surface", 1f);
            }

            if (material.HasProperty("_Blend"))
            {
                material.SetFloat("_Blend", 0f);
            }

            if (material.HasProperty("_ZWrite"))
            {
                material.SetFloat("_ZWrite", 0f);
            }

            if (material.HasProperty("_SrcBlend"))
            {
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            }

            if (material.HasProperty("_DstBlend"))
            {
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            }

            if (material.HasProperty("_Mode"))
            {
                material.SetFloat("_Mode", 3f);
            }

            material.EnableKeyword("_ALPHABLEND_ON");
            material.renderQueue = (int)RenderQueue.Transparent;
        }

        private static Mesh GetSphereMesh()
        {
            if (_sphereMesh != null)
            {
                return _sphereMesh;
            }

            GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            MeshFilter filter = temp.GetComponent<MeshFilter>();
            _sphereMesh = filter != null ? filter.sharedMesh : null;

            Collider col = temp.GetComponent<Collider>();
            if (col != null)
            {
                if (Application.isPlaying)
                {
                    Object.Destroy(col);
                }
                else
                {
                    Object.DestroyImmediate(col);
                }
            }

            if (Application.isPlaying)
            {
                Object.Destroy(temp);
            }
            else
            {
                Object.DestroyImmediate(temp);
            }

            return _sphereMesh;
        }

        private sealed class TimedDestroy : MonoBehaviour
        {
            public float Lifetime { get; set; } = 2f;

            private void OnEnable()
            {
                Destroy(gameObject, Mathf.Max(0.1f, Lifetime));
            }
        }

        private sealed class LightPulse : MonoBehaviour
        {
            public Light Target { get; set; }
            public float Duration { get; set; } = 0.2f;

            private float _startIntensity;
            private float _elapsed;

            private void OnEnable()
            {
                _elapsed = 0f;
                if (Target != null)
                {
                    _startIntensity = Target.intensity;
                }
            }

            private void Update()
            {
                if (Target == null)
                {
                    return;
                }

                _elapsed += Time.deltaTime;
                float duration = Mathf.Max(0.01f, Duration);
                float t = Mathf.Clamp01(_elapsed / duration);
                float fade = 1f - t * t;
                Target.intensity = _startIntensity * fade;
            }
        }
    }
}

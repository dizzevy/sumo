using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sumo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class SafeZoneVisualController : MonoBehaviour
    {
        [Header("Auto Build")]
        [SerializeField] private bool autoBuildIfMissing = true;
        [SerializeField] private bool disableLegacyVisualChildren = true;
        [SerializeField] private Transform visualRoot;
        [SerializeField] private MeshRenderer fillRenderer;
        [SerializeField] private MeshRenderer boundaryRenderer;
        [SerializeField] private ParticleSystem auraParticles;

        [Header("Colors")]
        [SerializeField] private Color fillColor = new Color(0.14f, 0.78f, 1f, 0.08f);
        [SerializeField] private Color boundaryColor = new Color(0.62f, 0.95f, 1f, 0.24f);
        [SerializeField] private Color particleColor = new Color(0.75f, 0.92f, 1f, 0.38f);

        [Header("Boundary Readability")]
        [SerializeField] private float boundaryScaleMultiplier = 1.008f;
        [SerializeField] private bool animateEmission = false;
        [SerializeField] private float emissionPulseSpeed = 2f;
        [SerializeField] private float emissionPulseAmplitude = 0.22f;

        private Material _fillMaterial;
        private Material _boundaryMaterial;
        private Material _particleMaterial;

        private float _radius = 1f;
        private bool _isActive;
        private int _phaseIndex;
        private bool _initialized;

        private static Mesh _sphereMesh;

        private void Awake()
        {
            NormalizeTransparency();
            EnsureVisualSetup();
            _initialized = true;
            ApplyVisualStateImmediate();
        }

        private void OnEnable()
        {
            if (_initialized)
            {
                ApplyVisualStateImmediate();
            }
        }

        private void Update()
        {
            if (!_initialized || !_isActive || !animateEmission)
            {
                return;
            }

            float phaseBoost = 1f + _phaseIndex * 0.08f;
            float pulse = 1f + Mathf.Sin(Time.time * Mathf.Max(0.01f, emissionPulseSpeed)) * emissionPulseAmplitude;

            SetEmission(_fillMaterial, fillColor * (1.15f * pulse * phaseBoost));
            SetEmission(_boundaryMaterial, boundaryColor * (1.85f * pulse * phaseBoost));
        }

        private void OnDestroy()
        {
            if (_fillMaterial != null)
            {
                Destroy(_fillMaterial);
            }

            if (_boundaryMaterial != null)
            {
                Destroy(_boundaryMaterial);
            }

            if (_particleMaterial != null)
            {
                Destroy(_particleMaterial);
            }
        }

        public void ApplyVisual(float radius, bool isActive, int phaseIndex)
        {
            _radius = Mathf.Max(0.25f, radius);
            _isActive = isActive;
            _phaseIndex = Mathf.Max(0, phaseIndex);
            ApplyVisualStateImmediate();
        }

        private void EnsureVisualSetup()
        {
            if (visualRoot == null)
            {
                Transform existing = transform.Find("VisualRoot");
                if (existing != null)
                {
                    visualRoot = existing;
                }
                else if (autoBuildIfMissing)
                {
                    GameObject root = new GameObject("VisualRoot");
                    visualRoot = root.transform;
                    visualRoot.SetParent(transform, false);
                }
            }

            if (visualRoot == null)
            {
                visualRoot = transform;
            }

            if (autoBuildIfMissing)
            {
                if (fillRenderer == null)
                {
                    fillRenderer = CreateSphereRenderer("FillSphere", visualRoot);
                }

                if (boundaryRenderer == null)
                {
                    boundaryRenderer = CreateSphereRenderer("BoundarySphere", visualRoot);
                }

                if (auraParticles == null)
                {
                    auraParticles = CreateAuraParticles("AuraParticles", visualRoot);
                }
            }

            if (disableLegacyVisualChildren)
            {
                DisableLegacyVisuals();
            }

            EnsureMaterials();
            AssignMaterials();
        }

        private void DisableLegacyVisuals()
        {
            ParticleSystemRenderer auraRenderer = auraParticles != null
                ? auraParticles.GetComponent<ParticleSystemRenderer>()
                : null;

            Renderer[] allRenderers = visualRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < allRenderers.Length; i++)
            {
                Renderer renderer = allRenderers[i];
                if (renderer == null)
                {
                    continue;
                }

                if (renderer == fillRenderer || renderer == boundaryRenderer || renderer == auraRenderer)
                {
                    continue;
                }

                string lowerName = renderer.gameObject.name.ToLowerInvariant();
                bool likelyLegacy = lowerName.Contains("ring")
                                    || lowerName.Contains("disc")
                                    || lowerName.Contains("disk")
                                    || lowerName.Contains("floor")
                                    || lowerName.Contains("plane")
                                    || lowerName.Contains("platform")
                                    || lowerName.Contains("shimmer")
                                    || lowerName.Contains("core");

                if (likelyLegacy || renderer is MeshRenderer)
                {
                    renderer.enabled = false;
                }
            }
        }

        private void ApplyVisualStateImmediate()
        {
            if (visualRoot == null)
            {
                return;
            }

            NormalizeTransparency();
            visualRoot.gameObject.SetActive(_isActive);

            float diameter = _radius * 2f;
            float boundaryScale = Mathf.Max(1.001f, boundaryScaleMultiplier);

            if (fillRenderer != null)
            {
                fillRenderer.transform.localScale = Vector3.one * diameter;
            }

            if (boundaryRenderer != null)
            {
                boundaryRenderer.transform.localScale = Vector3.one * diameter * boundaryScale;
            }

            if (_fillMaterial != null)
            {
                _fillMaterial.color = fillColor;
                SetEmission(_fillMaterial, fillColor * (1.1f + _phaseIndex * 0.04f));
            }

            if (_boundaryMaterial != null)
            {
                _boundaryMaterial.color = boundaryColor;
                SetEmission(_boundaryMaterial, boundaryColor * (1.75f + _phaseIndex * 0.08f));
            }

            if (auraParticles != null)
            {
                if (_isActive)
                {
                    ParticleSystem.ShapeModule shape = auraParticles.shape;
                    shape.shapeType = ParticleSystemShapeType.Sphere;
                    shape.radius = Mathf.Max(0.2f, _radius);

                    ParticleSystem.MainModule main = auraParticles.main;
                    main.startColor = new ParticleSystem.MinMaxGradient(
                        Color.Lerp(particleColor, boundaryColor, 0.25f),
                        Color.Lerp(fillColor, boundaryColor, 0.65f));

                    if (!auraParticles.isPlaying)
                    {
                        auraParticles.Play(true);
                    }
                }
                else if (auraParticles.isPlaying)
                {
                    auraParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
            }
        }

        private void EnsureMaterials()
        {
            if (_fillMaterial == null)
            {
                _fillMaterial = CreateTransparentMaterial("SafeZoneFillMaterial", fillColor, 1.1f);
            }

            if (_boundaryMaterial == null)
            {
                _boundaryMaterial = CreateTransparentMaterial("SafeZoneBoundaryMaterial", boundaryColor, 1.75f);
            }

            if (_particleMaterial == null)
            {
                Shader particleShader = FindBestShader(
                    "Universal Render Pipeline/Particles/Unlit",
                    "Universal Render Pipeline/Unlit",
                    "Particles/Standard Unlit",
                    "Sprites/Default",
                    "UI/Default",
                    "Unlit/Color",
                    "Standard");

                _particleMaterial = new Material(particleShader)
                {
                    name = "SafeZoneParticlesMaterial",
                    color = particleColor
                };

                SetupTransparentMaterial(_particleMaterial, particleColor, 1.35f);
            }
        }

        private void AssignMaterials()
        {
            if (fillRenderer != null)
            {
                fillRenderer.sharedMaterial = _fillMaterial;
            }

            if (boundaryRenderer != null)
            {
                boundaryRenderer.sharedMaterial = _boundaryMaterial;
            }

            if (auraParticles != null)
            {
                ParticleSystemRenderer auraRenderer = auraParticles.GetComponent<ParticleSystemRenderer>();
                if (auraRenderer != null)
                {
                    auraRenderer.sharedMaterial = _particleMaterial;
                }
            }
        }

        private static MeshRenderer CreateSphereRenderer(string objectName, Transform parent)
        {
            GameObject go = new GameObject(objectName);
            go.transform.SetParent(parent, false);

            MeshFilter filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = GetSphereMesh();

            return go.AddComponent<MeshRenderer>();
        }

        private static ParticleSystem CreateAuraParticles(string objectName, Transform parent)
        {
            GameObject go = new GameObject(objectName);
            go.transform.SetParent(parent, false);

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.duration = 2f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.55f, 1.1f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.08f, 0.32f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.14f);
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 120;
            main.gravityModifier = 0f;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 20f;

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 1f;

            ParticleSystem.NoiseModule noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.14f;
            noise.frequency = 0.4f;
            noise.scrollSpeed = 0.28f;

            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.View;
            return ps;
        }

        private static Material CreateTransparentMaterial(string materialName, Color baseColor, float emissionMultiplier)
        {
            Shader shader = FindBestShader(
                "Universal Render Pipeline/Unlit",
                "Universal Render Pipeline/Lit",
                "Sprites/Default",
                "UI/Default",
                "Unlit/Color",
                "Legacy Shaders/Transparent/Diffuse",
                "Standard");

            Material material = new Material(shader)
            {
                name = materialName,
                color = baseColor
            };

            SetupTransparentMaterial(material, baseColor, emissionMultiplier);
            return material;
        }

        private static void SetupTransparentMaterial(Material material, Color baseColor, float emissionMultiplier)
        {
            if (material == null)
            {
                return;
            }

            material.color = baseColor;
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", baseColor);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", baseColor);
            }

            if (material.HasProperty("_Surface"))
            {
                material.SetFloat("_Surface", 1f);
            }

            if (material.HasProperty("_Blend"))
            {
                material.SetFloat("_Blend", 0f);
            }

            if (material.HasProperty("_AlphaClip"))
            {
                material.SetFloat("_AlphaClip", 0f);
            }

            if (material.HasProperty("_SrcBlend"))
            {
                material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            }

            if (material.HasProperty("_DstBlend"))
            {
                material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            }

            if (material.HasProperty("_ZWrite"))
            {
                material.SetFloat("_ZWrite", 0f);
            }

            if (material.HasProperty("_Cull"))
            {
                material.SetFloat("_Cull", (float)CullMode.Off);
            }

            if (material.HasProperty("_Mode"))
            {
                material.SetFloat("_Mode", 3f);
            }

            material.DisableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_SURFACE_TYPE_OPAQUE");
            material.EnableKeyword("_EMISSION");
            SetEmission(material, baseColor * emissionMultiplier);
            material.renderQueue = (int)RenderQueue.Transparent;
        }

        private static void SetEmission(Material material, Color emissionColor)
        {
            if (material != null && material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", emissionColor);
            }
        }

        private static Shader FindBestShader(params string[] candidates)
        {
            Shader shader = FindFirstSupportedShader(candidates);
            if (shader != null)
            {
                return shader;
            }

            shader = FindFirstExistingShader(candidates);
            if (shader != null)
            {
                return shader;
            }

            shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                return shader;
            }

            shader = Shader.Find("UI/Default");
            if (shader != null)
            {
                return shader;
            }

            shader = Shader.Find("Unlit/Color");
            if (shader != null)
            {
                return shader;
            }

            return Shader.Find("Standard");
        }

        private static Shader FindFirstSupportedShader(string[] candidates)
        {
            if (candidates == null)
            {
                return null;
            }

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                Shader shader = Shader.Find(candidate);
                if (shader != null && shader.isSupported)
                {
                    return shader;
                }
            }

            return null;
        }

        private static Shader FindFirstExistingShader(string[] candidates)
        {
            if (candidates == null)
            {
                return null;
            }

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                Shader shader = Shader.Find(candidate);
                if (shader != null)
                {
                    return shader;
                }
            }

            return null;
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

            Collider collider = temp.GetComponent<Collider>();
            if (collider != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(collider);
                }
                else
                {
                    DestroyImmediate(collider);
                }
            }

            if (Application.isPlaying)
            {
                Destroy(temp);
            }
            else
            {
                DestroyImmediate(temp);
            }

            return _sphereMesh;
        }

        private void OnValidate()
        {
            boundaryScaleMultiplier = Mathf.Max(1.001f, boundaryScaleMultiplier);
            emissionPulseSpeed = Mathf.Max(0.01f, emissionPulseSpeed);
            emissionPulseAmplitude = Mathf.Clamp(emissionPulseAmplitude, 0f, 1f);
            NormalizeTransparency();

            if (!Application.isPlaying)
            {
                EnsureVisualSetup();
                ApplyVisualStateImmediate();
            }
        }

        private void NormalizeTransparency()
        {
            fillColor.a = Mathf.Clamp(fillColor.a, 0.03f, 0.18f);
            boundaryColor.a = Mathf.Clamp(boundaryColor.a, 0.08f, 0.34f);
            particleColor.a = Mathf.Clamp(particleColor.a, 0.08f, 0.5f);
        }
    }
}

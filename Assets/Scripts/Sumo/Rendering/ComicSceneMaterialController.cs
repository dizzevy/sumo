using System;
using System.Collections.Generic;
using Sumo.Gameplay;
using UnityEngine;

namespace Sumo.Rendering
{
    [DisallowMultipleComponent]
    public sealed class ComicSceneMaterialController : MonoBehaviour
    {
        [SerializeField] private bool applyOnAwake = true;
        [SerializeField] private bool includeInactive;
        [SerializeField] private bool skipDynamicGameplayObjects = true;
        [SerializeField] private bool preserveSourceHue = true;
        [SerializeField] private bool addMotionDriversToRigidbodies;

        private readonly Dictionary<Material, Material> _sourceMaterialCache = new Dictionary<Material, Material>();

        private Material _floor;
        private Material _ramp;
        private Material _wall;
        private Material _trim;
        private Material _prop;
        private Material _dark;
        private Shader _comicShader;

        private void Awake()
        {
            if (applyOnAwake)
            {
                ApplyComicMaterials();
            }
        }

        private void OnDestroy()
        {
            foreach (Material material in _sourceMaterialCache.Values)
            {
                if (material != null)
                {
                    Destroy(material);
                }
            }

            _sourceMaterialCache.Clear();
        }

        [ContextMenu("Apply Comic Materials")]
        public void ApplyComicMaterials()
        {
            LoadMaterials();

            Renderer[] renderers = FindObjectsByType<Renderer>(
                includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || ShouldSkip(renderer))
                {
                    continue;
                }

                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                for (int j = 0; j < materials.Length; j++)
                {
                    Material comicMaterial = ResolveMaterial(renderer, materials[j]);
                    if (comicMaterial == null)
                    {
                        continue;
                    }

                    if (materials[j] != comicMaterial)
                    {
                        materials[j] = comicMaterial;
                        changed = true;
                    }
                }

                if (changed)
                {
                    renderer.sharedMaterials = materials;
                }

                TryAttachMotionDriver(renderer);
            }
        }

        private void LoadMaterials()
        {
            _floor = Load("Comic_Floor");
            _ramp = Load("Comic_Ramp");
            _wall = Load("Comic_Wall");
            _trim = Load("Comic_Trim");
            _prop = Load("Comic_Prop");
            _dark = Load("Comic_Dark");
            _comicShader = Shader.Find("Sumo/ComicToon");
        }

        private static Material Load(string materialName)
        {
            return Resources.Load<Material>($"ComicMaterials/{materialName}");
        }

        private bool ShouldSkip(Renderer renderer)
        {
            if (renderer is ParticleSystemRenderer || renderer is SpriteRenderer)
            {
                return true;
            }

            Transform current = renderer.transform;
            while (current != null)
            {
                if (current.TryGetComponent(out SumoBallController _)
                    || current.TryGetComponent(out SafeZoneController _)
                    || current.TryGetComponent(out SafeZoneManager _))
                {
                    return true;
                }

                if (skipDynamicGameplayObjects
                    && (current.name.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0
                        || current.name.IndexOf("SafeZone", StringComparison.OrdinalIgnoreCase) >= 0
                        || current.name.IndexOf("Boundary", StringComparison.OrdinalIgnoreCase) >= 0
                        || current.name.IndexOf("FillSphere", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private Material ResolveMaterial(Renderer renderer, Material sourceMaterial)
        {
            Material fallback = ResolveFallbackMaterial(renderer);
            if (_comicShader == null)
            {
                return fallback != null ? fallback : sourceMaterial;
            }

            if (sourceMaterial == null)
            {
                return fallback;
            }

            if (_sourceMaterialCache.TryGetValue(sourceMaterial, out Material cached) && cached != null)
            {
                return cached;
            }

            Color sourceColor = ResolveSourceColor(sourceMaterial, fallback);
            Color comicColor = preserveSourceHue ? ResolveCartoonBaseColor(sourceColor) : sourceColor;

            Material material = new Material(_comicShader)
            {
                name = $"{sourceMaterial.name}_ComicHuePreserved",
                color = comicColor
            };

            ApplyComicStyle(material, fallback);
            ApplyColor(material, "_BaseColor", comicColor);
            ApplyColor(material, "_Color", comicColor);
            ApplyColor(material, "_ShadowColor", ResolvePaintedShadowColor(comicColor));
            ApplyColor(material, "_HighlightColor", ResolvePaintedHighlightColor(comicColor));
            ApplyColor(material, "_InkColor", ResolveInkColor(comicColor));

            _sourceMaterialCache[sourceMaterial] = material;
            return material;
        }

        private Color ResolveSourceColor(Material sourceMaterial, Material fallback)
        {
            Color tint = Color.white;
            bool hasTint = TryGetMaterialColor(sourceMaterial, out tint);

            if (TryGetTextureAverage(sourceMaterial, out Color average))
            {
                tint = hasTint ? MultiplyRgb(tint, average) : average;
                tint.a = hasTint ? tint.a : average.a;
                return tint;
            }

            if (hasTint)
            {
                return tint;
            }

            if (fallback != null && TryGetMaterialColor(fallback, out Color fallbackColor))
            {
                return fallbackColor;
            }

            return Color.gray;
        }

        private static bool TryGetMaterialColor(Material material, out Color color)
        {
            color = Color.white;
            if (material == null)
            {
                return false;
            }

            if (material.HasProperty("_BaseColor"))
            {
                color = material.GetColor("_BaseColor");
                return true;
            }

            if (material.HasProperty("_Color"))
            {
                color = material.GetColor("_Color");
                return true;
            }

            return false;
        }

        private static bool TryGetTextureAverage(Material material, out Color color)
        {
            color = Color.white;
            if (material == null)
            {
                return false;
            }

            Texture textureAsset = null;
            if (material.HasProperty("_BaseMap"))
            {
                textureAsset = material.GetTexture("_BaseMap");
            }
            else if (material.HasProperty("_MainTex"))
            {
                textureAsset = material.GetTexture("_MainTex");
            }

            Texture2D texture = textureAsset as Texture2D;
            if (texture == null)
            {
                return TryGetTextureAverageViaGpu(textureAsset, out color);
            }

            try
            {
                Color32[] pixels = texture.GetPixels32();
                if (pixels == null || pixels.Length == 0)
                {
                    return false;
                }

                double r = 0d;
                double g = 0d;
                double b = 0d;
                double a = 0d;
                int stride = Mathf.Max(1, pixels.Length / 512);
                int count = 0;
                for (int i = 0; i < pixels.Length; i += stride)
                {
                    Color32 pixel = pixels[i];
                    r += pixel.r;
                    g += pixel.g;
                    b += pixel.b;
                    a += pixel.a;
                    count++;
                }

                double scale = 1d / (255d * Math.Max(1, count));
                color = new Color((float)(r * scale), (float)(g * scale), (float)(b * scale), (float)(a * scale));
                return true;
            }
            catch (UnityException)
            {
                return TryGetTextureAverageViaGpu(textureAsset, out color);
            }
        }

        private static bool TryGetTextureAverageViaGpu(Texture texture, out Color color)
        {
            color = Color.white;
            if (texture == null)
            {
                return false;
            }

            const int SampleSize = 16;
            RenderTexture previous = RenderTexture.active;
            RenderTexture temporary = null;
            Texture2D readback = null;

            try
            {
                temporary = RenderTexture.GetTemporary(
                    SampleSize,
                    SampleSize,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default);

                Graphics.Blit(texture, temporary);
                RenderTexture.active = temporary;

                readback = new Texture2D(SampleSize, SampleSize, TextureFormat.RGBA32, false, false);
                readback.ReadPixels(new Rect(0, 0, SampleSize, SampleSize), 0, 0, false);
                readback.Apply(false, false);

                Color32[] pixels = readback.GetPixels32();
                double r = 0d;
                double g = 0d;
                double b = 0d;
                double a = 0d;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 pixel = pixels[i];
                    r += pixel.r;
                    g += pixel.g;
                    b += pixel.b;
                    a += pixel.a;
                }

                double scale = 1d / (255d * Math.Max(1, pixels.Length));
                color = new Color((float)(r * scale), (float)(g * scale), (float)(b * scale), (float)(a * scale));
                return true;
            }
            catch (UnityException)
            {
                return false;
            }
            finally
            {
                RenderTexture.active = previous;

                if (temporary != null)
                {
                    RenderTexture.ReleaseTemporary(temporary);
                }

                if (readback != null)
                {
                    if (Application.isPlaying)
                    {
                        Destroy(readback);
                    }
                    else
                    {
                        DestroyImmediate(readback);
                    }
                }
            }
        }

        private static Color ResolveCartoonBaseColor(Color color)
        {
            Color.RGBToHSV(color, out float h, out float s, out float v);

            if (s <= 0.05f)
            {
                float gray = Quantize(Mathf.Clamp(v * 1.04f + 0.03f, 0.08f, 0.92f), 6);
                return new Color(gray, gray, gray, color.a);
            }

            float simplifiedS = Quantize(Mathf.Clamp01(s + 0.16f), 6);
            float simplifiedV = Quantize(Mathf.Clamp(v * 1.06f + 0.03f, 0.18f, 0.94f), 6);
            Color simplified = Color.HSVToRGB(h, Mathf.Clamp01(simplifiedS), Mathf.Clamp01(simplifiedV));
            simplified.a = color.a;
            return simplified;
        }

        private static float Quantize(float value, int steps)
        {
            float clamped = Mathf.Clamp01(value);
            float maxStep = Mathf.Max(1, steps - 1);
            return Mathf.Round(clamped * maxStep) / maxStep;
        }

        private static Color ResolvePaintedShadowColor(Color sourceColor)
        {
            Color.RGBToHSV(sourceColor, out float h, out float s, out float v);
            if (s <= 0.05f)
            {
                float gray = Mathf.Clamp(v * 0.48f, 0.08f, 0.42f);
                return new Color(gray, gray, gray, 1f);
            }

            float shadowSaturation = Mathf.Clamp01(s + 0.18f);
            float shadowValue = Mathf.Clamp(v * 0.46f, 0.14f, 0.42f);
            Color shadow = Color.HSVToRGB(h, shadowSaturation, shadowValue);
            shadow.a = 1f;
            return shadow;
        }

        private static Color ResolvePaintedHighlightColor(Color sourceColor)
        {
            Color.RGBToHSV(sourceColor, out float h, out float s, out float v);
            if (s <= 0.05f)
            {
                float gray = Mathf.Clamp(v * 1.24f + 0.08f, 0.38f, 1f);
                return new Color(gray, gray, gray, 1f);
            }

            float highlightSaturation = Mathf.Clamp01(s * 0.78f);
            float highlightValue = Mathf.Clamp(v * 1.34f + 0.08f, 0.72f, 1f);
            Color highlight = Color.HSVToRGB(h, highlightSaturation, highlightValue);
            highlight.a = 1f;
            return highlight;
        }

        private static Color ResolveInkColor(Color sourceColor)
        {
            Color ink = Color.Lerp(sourceColor, new Color(0.006f, 0.005f, 0.012f, 1f), 0.88f);
            ink.r = Mathf.Min(ink.r, 0.035f);
            ink.g = Mathf.Min(ink.g, 0.032f);
            ink.b = Mathf.Min(ink.b, 0.045f);
            ink.a = 1f;
            return ink;
        }

        private static Color MultiplyRgb(Color a, Color b)
        {
            return new Color(a.r * b.r, a.g * b.g, a.b * b.b, a.a * b.a);
        }

        private static void ApplyComicStyle(Material material, Material fallback)
        {
            CopyFloat(material, fallback, "_InkWidth", 2.8f);
            CopyFloat(material, fallback, "_ShadeSteps", 4f);
            SetFloat(material, "_HalftoneStrength", 0f);
            SetFloat(material, "_HalftoneScale", 0f);
            CopyFloat(material, fallback, "_ComicMotionShadowStrength", 0.1f);
            SetFloat(material, "_CastShadowPatternStrength", 0f);
            CopyFloat(material, fallback, "_CastShadowPatternScale", 3.4f);
            CopyFloat(material, fallback, "_CastShadowPosterizeSteps", 4f);
            CopyFloat(material, fallback, "_PatchStrength", 0.28f);
            CopyFloat(material, fallback, "_PatchScale", 3.4f);
            CopyFloat(material, fallback, "_PatchSoftness", 0.62f);
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property))
            {
                material.SetFloat(property, value);
            }
        }

        private static void CopyFloat(Material target, Material source, string property, float fallback)
        {
            if (target == null || !target.HasProperty(property))
            {
                return;
            }

            float value = source != null && source.HasProperty(property) ? source.GetFloat(property) : fallback;
            target.SetFloat(property, value);
        }

        private static void CopyColor(Material target, Material source, string property, Color fallback)
        {
            if (target == null || !target.HasProperty(property))
            {
                return;
            }

            Color value = source != null && source.HasProperty(property) ? source.GetColor(property) : fallback;
            target.SetColor(property, value);
        }

        private static void ApplyColor(Material material, string property, Color color)
        {
            if (material != null && material.HasProperty(property))
            {
                material.SetColor(property, color);
            }
        }

        private void TryAttachMotionDriver(Renderer renderer)
        {
            if (!addMotionDriversToRigidbodies || renderer == null)
            {
                return;
            }

            Rigidbody body = renderer.GetComponentInParent<Rigidbody>();
            if (body == null || body.isKinematic)
            {
                return;
            }

            if (!body.TryGetComponent(out ComicMotionShadowDriver _))
            {
                body.gameObject.AddComponent<ComicMotionShadowDriver>();
            }
        }

        private Material ResolveFallbackMaterial(Renderer renderer)
        {
            string name = GetSearchName(renderer);

            if (Contains(name, "floor") || Contains(name, "plane"))
            {
                return _floor;
            }

            if (Contains(name, "ramp"))
            {
                return _ramp;
            }

            if (Contains(name, "wall") || Contains(name, "side") || Contains(name, "barrier"))
            {
                return _wall;
            }

            if (Contains(name, "trim") || Contains(name, "edge") || Contains(name, "rail") || Contains(name, "line"))
            {
                return _trim;
            }

            if (Contains(name, "dark") || Contains(name, "shadow") || Contains(name, "base"))
            {
                return _dark;
            }

            return _prop;
        }

        private static string GetSearchName(Renderer renderer)
        {
            string result = renderer.gameObject.name;
            Material sharedMaterial = renderer.sharedMaterial;
            if (sharedMaterial != null)
            {
                result += " " + sharedMaterial.name;
            }

            Transform parent = renderer.transform.parent;
            if (parent != null)
            {
                result += " " + parent.name;
            }

            return result;
        }

        private static bool Contains(string value, string token)
        {
            return value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

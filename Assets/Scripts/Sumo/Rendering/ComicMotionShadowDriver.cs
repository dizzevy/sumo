using UnityEngine;

namespace Sumo.Rendering
{
    [DisallowMultipleComponent]
    public sealed class ComicMotionShadowDriver : MonoBehaviour
    {
        [SerializeField] private bool includeChildren = true;
        [SerializeField] private float referenceSpeed = 10f;
        [SerializeField] private float smoothing = 10f;
        [SerializeField] private float motionShadowStrength = 0.1f;

        private static readonly int MotionDirectionId = Shader.PropertyToID("_ComicMotionDirection");
        private static readonly int MotionAmountId = Shader.PropertyToID("_ComicMotionAmount");
        private static readonly int ShadePhaseId = Shader.PropertyToID("_ComicShadePhase");
        private static readonly int MotionShadowStrengthId = Shader.PropertyToID("_ComicMotionShadowStrength");

        private Rigidbody _rigidbody;
        private Renderer[] _renderers;
        private MaterialPropertyBlock _propertyBlock;
        private Vector3 _smoothedDirection = Vector3.forward;
        private float _smoothedAmount;

        private void Awake()
        {
            CacheComponents();
        }

        private void OnEnable()
        {
            CacheComponents();
        }

        private void LateUpdate()
        {
            if (_rigidbody == null || _renderers == null || _renderers.Length == 0)
            {
                return;
            }

            Vector3 velocity = _rigidbody.linearVelocity;
            velocity.y = 0f;

            float speed = velocity.magnitude;
            float targetAmount = Mathf.Clamp01(speed / Mathf.Max(0.01f, referenceSpeed));
            float blend = 1f - Mathf.Exp(-Mathf.Max(0.01f, smoothing) * Time.deltaTime);
            _smoothedAmount = Mathf.Lerp(_smoothedAmount, targetAmount, blend);

            if (speed > 0.025f)
            {
                _smoothedDirection = Vector3.Slerp(_smoothedDirection, velocity / speed, blend).normalized;
            }

            ApplyProperties();
        }

        private void CacheComponents()
        {
            if (_rigidbody == null)
            {
                _rigidbody = GetComponent<Rigidbody>();
            }

            _renderers = includeChildren
                ? GetComponentsInChildren<Renderer>(true)
                : GetComponents<Renderer>();

            _propertyBlock ??= new MaterialPropertyBlock();
        }

        private void ApplyProperties()
        {
            Vector4 direction = new Vector4(_smoothedDirection.x, _smoothedDirection.y, _smoothedDirection.z, 0f);
            float phase = Vector3.Dot(transform.position, new Vector3(0.37f, 0f, 0.29f));

            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer target = _renderers[i];
                if (target == null || target is ParticleSystemRenderer || target is SpriteRenderer)
                {
                    continue;
                }

                target.GetPropertyBlock(_propertyBlock);
                _propertyBlock.SetVector(MotionDirectionId, direction);
                _propertyBlock.SetFloat(MotionAmountId, _smoothedAmount);
                _propertyBlock.SetFloat(ShadePhaseId, phase);
                _propertyBlock.SetFloat(MotionShadowStrengthId, motionShadowStrength);
                target.SetPropertyBlock(_propertyBlock);
            }
        }

        private void OnValidate()
        {
            referenceSpeed = Mathf.Max(0.01f, referenceSpeed);
            smoothing = Mathf.Max(0.01f, smoothing);
            motionShadowStrength = Mathf.Clamp01(motionShadowStrength);
        }
    }
}

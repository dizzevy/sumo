using Fusion;
using UnityEngine;

namespace Sumo.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(SphereCollider))]
    public sealed class SafeZoneController : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private SphereCollider triggerCollider;
        [SerializeField] private SafeZoneVisualController visualController;

        [Header("Defaults")]
        [SerializeField] private float defaultRadius = 10f;

        [Networked] public NetworkBool IsZoneActive { get; private set; }
        [Networked] public Vector3 ZoneCenter { get; private set; }
        [Networked] public float ZoneRadius { get; private set; }
        [Networked] public int ZonePhaseIndex { get; private set; }

        private bool _stateApplied;
        private NetworkBool _lastAppliedActive;
        private Vector3 _lastAppliedCenter;
        private float _lastAppliedRadius;
        private int _lastAppliedPhase;

        public override void Spawned()
        {
            EnsureReferences();
            if (HasStateAuthority && ZoneRadius <= 0.001f)
            {
                ZoneRadius = Mathf.Max(0.5f, defaultRadius);
                ZoneCenter = transform.position;
                ZonePhaseIndex = 0;
                IsZoneActive = false;
            }

            ApplyState(force: true);
        }

        public override void Render()
        {
            ApplyState(force: false);
        }

        public void ServerSetZone(Vector3 center, float radius, int phaseIndex, bool active)
        {
            if (!HasStateAuthority)
            {
                return;
            }

            ZoneCenter = center;
            ZoneRadius = Mathf.Max(0.25f, radius);
            ZonePhaseIndex = Mathf.Max(0, phaseIndex);
            IsZoneActive = active;
            ApplyState(force: true);
        }

        public void ServerHideZone()
        {
            if (!HasStateAuthority)
            {
                return;
            }

            IsZoneActive = false;
            ApplyState(force: true);
        }

        public bool ContainsWorldPosition(Vector3 worldPosition)
        {
            if (!IsZoneActive)
            {
                return false;
            }

            float radius = Mathf.Max(0.01f, ZoneRadius);
            return Vector3.SqrMagnitude(worldPosition - ZoneCenter) <= radius * radius;
        }

        private void EnsureReferences()
        {
            if (triggerCollider == null)
            {
                triggerCollider = GetComponent<SphereCollider>();
            }

            if (visualController == null)
            {
                visualController = GetComponentInChildren<SafeZoneVisualController>(true);
            }

            if (visualController == null)
            {
                GameObject visualObject = new GameObject("SafeZoneVisual");
                visualObject.transform.SetParent(transform, false);
                visualController = visualObject.AddComponent<SafeZoneVisualController>();
            }

            if (triggerCollider != null)
            {
                triggerCollider.isTrigger = true;
                triggerCollider.radius = 1f;
                triggerCollider.center = Vector3.zero;
            }
        }

        private void ApplyState(bool force)
        {
            if (!force && _stateApplied
                && _lastAppliedActive == IsZoneActive
                && _lastAppliedCenter == ZoneCenter
                && Mathf.Abs(_lastAppliedRadius - ZoneRadius) <= 0.0001f
                && _lastAppliedPhase == ZonePhaseIndex)
            {
                return;
            }

            EnsureReferences();

            transform.position = ZoneCenter;

            if (triggerCollider != null)
            {
                triggerCollider.enabled = IsZoneActive;
                triggerCollider.radius = Mathf.Max(0.25f, ZoneRadius);
            }

            if (visualController != null)
            {
                visualController.ApplyVisual(Mathf.Max(0.25f, ZoneRadius), IsZoneActive, ZonePhaseIndex);
            }

            _lastAppliedActive = IsZoneActive;
            _lastAppliedCenter = ZoneCenter;
            _lastAppliedRadius = ZoneRadius;
            _lastAppliedPhase = ZonePhaseIndex;
            _stateApplied = true;
        }

        private void OnValidate()
        {
            defaultRadius = Mathf.Max(0.25f, defaultRadius);
            EnsureReferences();
        }
    }
}

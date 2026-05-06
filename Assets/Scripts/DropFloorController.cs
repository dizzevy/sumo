using Fusion;
using UnityEngine;

namespace Sumo.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class DropFloorController : NetworkBehaviour
    {
        [Header("Auto-discovery")]
        [SerializeField] private bool autoFindColliders = true;
        [SerializeField] private bool autoFindRenderers = true;

        [Header("Controlled Components")]
        [SerializeField] private Collider[] controlledColliders;
        [SerializeField] private Renderer[] controlledRenderers;

        [Networked] public NetworkBool IsClosed { get; private set; } = true;

        private bool _hasAppliedState;
        private bool _lastAppliedClosed;
        private bool _loggedColliderRecovery;
        private bool _loggedRendererRecovery;

        public override void Spawned()
        {
            CacheComponentsIfNeeded();
            ApplyStateIfChanged(force: true);
        }

        public override void Render()
        {
            ApplyStateIfChanged(force: false);
        }

        public void SetClosedState(bool closed)
        {
            if (!HasStateAuthority)
            {
                return;
            }

            IsClosed = closed;
            ApplyStateIfChanged(force: true);
        }

        private void CacheComponentsIfNeeded()
        {
            if (autoFindColliders && NeedsComponentRefresh(controlledColliders))
            {
                if (controlledColliders != null && controlledColliders.Length > 0 && !_loggedColliderRecovery)
                {
                    Debug.LogWarning($"{nameof(DropFloorController)} on {name}: controlled collider references contained missing entries; rebuilding from children.");
                    _loggedColliderRecovery = true;
                }

                controlledColliders = GetComponentsInChildren<Collider>(includeInactive: true);
            }

            if (autoFindRenderers && NeedsComponentRefresh(controlledRenderers))
            {
                if (controlledRenderers != null && controlledRenderers.Length > 0 && !_loggedRendererRecovery)
                {
                    Debug.LogWarning($"{nameof(DropFloorController)} on {name}: controlled renderer references contained missing entries; rebuilding from children.");
                    _loggedRendererRecovery = true;
                }

                controlledRenderers = GetComponentsInChildren<Renderer>(includeInactive: true);
            }
        }

        private static bool NeedsComponentRefresh<T>(T[] components) where T : Component
        {
            if (components == null || components.Length == 0)
            {
                return true;
            }

            int validCount = 0;
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] != null)
                {
                    validCount++;
                }
            }

            return validCount == 0 || validCount < components.Length;
        }

        private void ApplyStateIfChanged(bool force)
        {
            bool closed = IsClosed;
            if (!force && _hasAppliedState && _lastAppliedClosed == closed)
            {
                return;
            }

            CacheComponentsIfNeeded();

            if (controlledColliders != null)
            {
                for (int i = 0; i < controlledColliders.Length; i++)
                {
                    Collider col = controlledColliders[i];
                    if (col != null)
                    {
                        col.enabled = closed;
                    }
                }
            }

            if (controlledRenderers != null)
            {
                for (int i = 0; i < controlledRenderers.Length; i++)
                {
                    Renderer renderer = controlledRenderers[i];
                    if (renderer != null)
                    {
                        renderer.enabled = closed;
                    }
                }
            }

            _lastAppliedClosed = closed;
            _hasAppliedState = true;
        }

        private void OnValidate()
        {
            CacheComponentsIfNeeded();
        }
    }
}

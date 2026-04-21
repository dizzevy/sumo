using UnityEngine;

namespace Sumo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class ZoneExitKillBoundary : MonoBehaviour
    {
        private const string LegacyWallsRootName = "__ZoneExitKillWalls";

        [Header("References")]
        [SerializeField] private MatchRoundManager matchRoundManager;

        [Header("Trigger Volume (local space)")]
        [SerializeField] private bool autoAddBoxColliderIfMissing = true;
        [SerializeField] private bool forceTrigger = true;
        [SerializeField] private Vector3 boxCenter = Vector3.zero;
        [SerializeField] private Vector3 boxSize = new Vector3(42f, 6f, 42f);
        [SerializeField] private bool drawGizmo = true;

        [SerializeField, HideInInspector] private Collider triggerCollider;

        private void Reset()
        {
            EnsureReferences();
            EnsureTriggerCollider(applyBoxShape: true);
            CleanupLegacyWalls();
        }

        private void Awake()
        {
            EnsureReferences();
            EnsureTriggerCollider(applyBoxShape: false);
            CleanupLegacyWalls();
        }

        private void OnValidate()
        {
            boxSize = new Vector3(
                Mathf.Max(0.1f, boxSize.x),
                Mathf.Max(0.1f, boxSize.y),
                Mathf.Max(0.1f, boxSize.z));

            if (Application.isPlaying)
            {
                return;
            }

            EnsureReferences();
            EnsureTriggerCollider(applyBoxShape: true);
            CleanupLegacyWalls();
        }

        private void EnsureReferences()
        {
            if (matchRoundManager == null)
            {
                matchRoundManager = FindObjectOfType<MatchRoundManager>(true);
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            TryEliminateFromTrigger(other);
        }

        private void OnTriggerStay(Collider other)
        {
            TryEliminateFromTrigger(other);
        }

        private void TryEliminateFromTrigger(Collider other)
        {
            if (other == null || other.isTrigger)
            {
                return;
            }

            PlayerRoundState playerRoundState = ResolvePlayerRoundState(other);
            if (playerRoundState == null)
            {
                return;
            }

            EnsureReferences();
            if (matchRoundManager != null)
            {
                matchRoundManager.ServerEliminatePlayerFromBoundary(playerRoundState);
            }
        }

        private void EnsureTriggerCollider(bool applyBoxShape)
        {
            if (triggerCollider == null)
            {
                triggerCollider = GetComponent<Collider>();
            }

            if (triggerCollider == null && autoAddBoxColliderIfMissing)
            {
                triggerCollider = gameObject.AddComponent<BoxCollider>();
            }

            if (triggerCollider == null)
            {
                return;
            }

            if (forceTrigger)
            {
                triggerCollider.isTrigger = true;
            }

            if (applyBoxShape && triggerCollider is BoxCollider boxCollider)
            {
                boxCollider.center = boxCenter;
                boxCollider.size = boxSize;
            }
        }

        private void CleanupLegacyWalls()
        {
            Transform legacyRoot = transform.Find(LegacyWallsRootName);
            if (legacyRoot == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(legacyRoot.gameObject);
            }
            else
            {
                DestroyImmediate(legacyRoot.gameObject);
            }
        }

        private static PlayerRoundState ResolvePlayerRoundState(Collider other)
        {
            if (other.attachedRigidbody != null)
            {
                PlayerRoundState rbState = other.attachedRigidbody.GetComponent<PlayerRoundState>();
                if (rbState != null)
                {
                    return rbState;
                }
            }

            PlayerRoundState directState = other.GetComponent<PlayerRoundState>();
            if (directState != null)
            {
                return directState;
            }

            return other.GetComponentInParent<PlayerRoundState>();
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawGizmo)
            {
                return;
            }

            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.25f, 0.12f, 0.9f);

            Vector3 center = boxCenter;
            Vector3 size = boxSize;
            if (triggerCollider is BoxCollider boxCollider)
            {
                center = boxCollider.center;
                size = boxCollider.size;
            }

            Gizmos.DrawWireCube(center, size);
        }
    }
}

using UnityEngine;

namespace Sumo.Online
{
    [DisallowMultipleComponent]
    public sealed class SpawnPoint : MonoBehaviour
    {
        [SerializeField] private int order;

        public int Order => order;
        public Vector3 Position => transform.position;
        public Quaternion Rotation => transform.rotation;

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, 0.5f);

            Gizmos.color = Color.cyan;
            Vector3 forward = transform.forward * 1.2f;
            Gizmos.DrawLine(transform.position, transform.position + forward);
        }
    }
}

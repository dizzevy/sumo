using UnityEngine;

namespace Sumo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class SafeZonePoint : MonoBehaviour
    {
        [SerializeField] private int order;
        [SerializeField] private float pointRadius = 0.75f;

        public int Order => order;
        public Vector3 Position => transform.position;
        public Quaternion Rotation => transform.rotation;
        public float PointRadius => Mathf.Max(0f, pointRadius);

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.95f, 1f, 0.35f);
            Gizmos.DrawSphere(transform.position, Mathf.Max(0.15f, pointRadius));
            Gizmos.color = new Color(0.2f, 0.95f, 1f, 0.95f);
            Gizmos.DrawWireSphere(transform.position, Mathf.Max(0.15f, pointRadius));
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.1f, 1f, 0.8f, 1f);
            Gizmos.DrawLine(transform.position, transform.position + transform.up * 1.4f);
        }
    }
}

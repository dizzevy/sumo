using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sumo.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class PreRoundBoxSpawner : MonoBehaviour
    {
        [Header("Spawn Points (inside start box)")]
        [SerializeField] private Transform[] spawnPoints;
        [SerializeField] private bool autoCollectChildPointsIfEmpty = true;

        [Header("Fallback Spawn Ring")]
        [SerializeField] private float fallbackRadius = 2.5f;
        [SerializeField] private float fallbackHeightOffset = 0.5f;

        [Header("Spectator Hold Point")]
        [SerializeField] private Transform spectatorHoldPoint;
        [SerializeField] private Vector3 spectatorFallbackOffset = new Vector3(0f, 12f, 0f);

        private readonly List<Transform> _cachedPoints = new List<Transform>(16);

        public int SpawnPointCount
        {
            get
            {
                RefreshCacheIfNeeded();
                return _cachedPoints.Count;
            }
        }

        public bool TryGetSpawnPose(int index, out Vector3 position, out Quaternion rotation)
        {
            RefreshCacheIfNeeded();

            if (_cachedPoints.Count > 0)
            {
                int normalized = Mathf.Abs(index) % _cachedPoints.Count;
                Transform point = _cachedPoints[normalized];
                if (point != null)
                {
                    position = point.position;
                    rotation = point.rotation;
                    return true;
                }
            }

            float angle = Mathf.Abs(index) * 2.39996323f;
            float radius = Mathf.Max(0.25f, fallbackRadius);
            Vector3 horizontal = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            position = transform.position + horizontal + Vector3.up * fallbackHeightOffset;
            rotation = transform.rotation;
            return true;
        }

        public void GetSpectatorHoldPose(int index, out Vector3 position, out Quaternion rotation)
        {
            if (spectatorHoldPoint != null)
            {
                position = spectatorHoldPoint.position;
                rotation = spectatorHoldPoint.rotation;
                return;
            }

            float angle = Mathf.Abs(index) * 2.39996323f;
            Vector3 radial = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 1.8f;
            position = transform.position + spectatorFallbackOffset + radial;
            rotation = Quaternion.identity;
        }

        private void Awake()
        {
            RebuildCache();
        }

        private void OnValidate()
        {
            fallbackRadius = Mathf.Max(0f, fallbackRadius);
            RebuildCache();
        }

        private void RefreshCacheIfNeeded()
        {
            if (_cachedPoints.Count == 0)
            {
                RebuildCache();
            }
        }

        private void RebuildCache()
        {
            _cachedPoints.Clear();

            if (spawnPoints != null)
            {
                for (int i = 0; i < spawnPoints.Length; i++)
                {
                    if (spawnPoints[i] != null)
                    {
                        _cachedPoints.Add(spawnPoints[i]);
                    }
                }
            }

            if (_cachedPoints.Count > 0 || !autoCollectChildPointsIfEmpty)
            {
                return;
            }

            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child == null || child == spectatorHoldPoint)
                {
                    continue;
                }

                _cachedPoints.Add(child);
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.25f, 0.9f, 1f, 0.9f);
            RefreshCacheIfNeeded();

            if (_cachedPoints.Count == 0)
            {
                Gizmos.DrawWireSphere(transform.position + Vector3.up * fallbackHeightOffset, fallbackRadius);
            }
            else
            {
                for (int i = 0; i < _cachedPoints.Count; i++)
                {
                    Transform point = _cachedPoints[i];
                    if (point == null)
                    {
                        continue;
                    }

                    Gizmos.DrawWireSphere(point.position, 0.35f);
                    Gizmos.DrawLine(point.position, point.position + point.forward * 0.8f);
                }
            }

            if (spectatorHoldPoint != null)
            {
                Gizmos.color = new Color(1f, 0.7f, 0.1f, 0.9f);
                Gizmos.DrawWireSphere(spectatorHoldPoint.position, 0.45f);
            }
        }
    }
}

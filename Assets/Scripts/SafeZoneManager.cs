using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Sumo.Gameplay
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class SafeZoneManager : NetworkBehaviour
    {
        [Header("References")]
        [SerializeField] private SafeZoneController safeZoneController;
        [SerializeField] private SafeZonePoint[] configuredPoints;
        [SerializeField] private bool autoFindPointsIfListEmpty = true;

        [Header("Zone Progression")]
        [SerializeField] private float initialRadius = 14f;
        [SerializeField] private float shrinkMultiplier = 0.72f;
        [SerializeField] private float minimumRadius = 3.5f;
        [SerializeField] private bool disallowImmediatePointRepeat = true;

        [Header("Randomization")]
        [SerializeField] private int randomSeedOffset = 13579;

        [Networked] public int CurrentZoneStep { get; private set; }
        [Networked] public float CurrentRadius { get; private set; }
        [Networked] public int CurrentPointIndex { get; private set; }

        private readonly List<SafeZonePoint> _points = new List<SafeZonePoint>(64);
        private System.Random _random;
        private int _lastChosenIndex = -1;

        public override void Spawned()
        {
            EnsureReferences();
            RefreshPoints();

            if (HasStateAuthority && safeZoneController != null)
            {
                safeZoneController.ServerHideZone();
                CurrentZoneStep = 0;
                CurrentRadius = Mathf.Max(0.25f, initialRadius);
                CurrentPointIndex = -1;
            }
        }

        public void ServerResetForRound(int roundNumber)
        {
            if (!HasStateAuthority)
            {
                return;
            }

            EnsureReferences();
            RefreshPoints();

            int seed = unchecked(roundNumber * 73856093 + randomSeedOffset);
            _random = new System.Random(seed);
            _lastChosenIndex = -1;

            CurrentZoneStep = 0;
            CurrentRadius = Mathf.Max(0.25f, initialRadius);
            CurrentPointIndex = -1;

            if (safeZoneController != null)
            {
                safeZoneController.ServerHideZone();
            }
        }

        public bool ServerSpawnFirstZone()
        {
            if (!HasStateAuthority)
            {
                return false;
            }

            CurrentZoneStep = 1;
            CurrentRadius = Mathf.Max(0.25f, initialRadius);
            return TrySpawnCurrentStepZone();
        }

        public bool ServerSpawnNextZone()
        {
            if (!HasStateAuthority)
            {
                return false;
            }

            CurrentZoneStep = Mathf.Max(1, CurrentZoneStep + 1);
            float nextRadius = CurrentRadius <= 0.01f
                ? initialRadius
                : CurrentRadius * Mathf.Clamp(shrinkMultiplier, 0.05f, 0.99f);
            CurrentRadius = Mathf.Max(minimumRadius, nextRadius);
            return TrySpawnCurrentStepZone();
        }

        public void ServerHideCurrentZone()
        {
            if (!HasStateAuthority)
            {
                return;
            }

            if (safeZoneController != null)
            {
                safeZoneController.ServerHideZone();
            }
        }

        public bool IsInsideCurrentZone(Vector3 worldPosition)
        {
            return safeZoneController != null && safeZoneController.ContainsWorldPosition(worldPosition);
        }

        public float GetCurrentRadiusOrDefault()
        {
            if (CurrentRadius > 0.01f)
            {
                return CurrentRadius;
            }

            return Mathf.Max(0.25f, initialRadius);
        }

        private bool TrySpawnCurrentStepZone()
        {
            EnsureReferences();
            RefreshPoints();

            if (safeZoneController == null || _points.Count == 0)
            {
                return false;
            }

            if (_random == null)
            {
                int seed = unchecked(Runner.Tick.Raw * 19349663 + randomSeedOffset);
                _random = new System.Random(seed);
            }

            int index = ChoosePointIndex();
            if (index < 0 || index >= _points.Count)
            {
                return false;
            }

            SafeZonePoint point = _points[index];
            if (point == null)
            {
                return false;
            }

            CurrentPointIndex = index;
            _lastChosenIndex = index;

            Vector3 center = point.Position + GetLocalJitter(point.PointRadius);
            safeZoneController.ServerSetZone(
                center,
                Mathf.Max(minimumRadius, CurrentRadius),
                Mathf.Max(0, CurrentZoneStep - 1),
                active: true);

            return true;
        }

        private int ChoosePointIndex()
        {
            int count = _points.Count;
            if (count == 0)
            {
                return -1;
            }

            if (count == 1)
            {
                return 0;
            }

            int candidate = _random.Next(0, count);

            if (!disallowImmediatePointRepeat || _lastChosenIndex < 0)
            {
                return candidate;
            }

            if (candidate == _lastChosenIndex)
            {
                candidate = (candidate + 1 + _random.Next(0, count - 1)) % count;
            }

            return candidate;
        }

        private Vector3 GetLocalJitter(float radius)
        {
            float effective = Mathf.Max(0f, radius);
            if (effective <= 0.001f)
            {
                return Vector3.zero;
            }

            float angle = (float)(_random.NextDouble() * Mathf.PI * 2f);
            float dist = (float)(_random.NextDouble()) * effective;
            return new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);
        }

        private void EnsureReferences()
        {
            if (safeZoneController == null)
            {
                safeZoneController = FindObjectOfType<SafeZoneController>(true);
            }
        }

        private void RefreshPoints()
        {
            _points.Clear();

            if (configuredPoints != null)
            {
                for (int i = 0; i < configuredPoints.Length; i++)
                {
                    SafeZonePoint point = configuredPoints[i];
                    if (point != null)
                    {
                        _points.Add(point);
                    }
                }
            }

            if (_points.Count == 0 && autoFindPointsIfListEmpty)
            {
                SafeZonePoint[] found = FindObjectsOfType<SafeZonePoint>(true);
                if (found != null)
                {
                    for (int i = 0; i < found.Length; i++)
                    {
                        if (found[i] != null)
                        {
                            _points.Add(found[i]);
                        }
                    }
                }
            }

            _points.Sort(ComparePoints);
        }

        private static int ComparePoints(SafeZonePoint a, SafeZonePoint b)
        {
            if (ReferenceEquals(a, b))
            {
                return 0;
            }

            if (a == null)
            {
                return 1;
            }

            if (b == null)
            {
                return -1;
            }

            int orderCompare = a.Order.CompareTo(b.Order);
            if (orderCompare != 0)
            {
                return orderCompare;
            }

            return string.Compare(a.name, b.name, StringComparison.Ordinal);
        }

        private void OnValidate()
        {
            initialRadius = Mathf.Max(0.25f, initialRadius);
            shrinkMultiplier = Mathf.Clamp(shrinkMultiplier, 0.05f, 0.99f);
            minimumRadius = Mathf.Max(0.25f, minimumRadius);
        }
    }
}

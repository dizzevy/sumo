using Fusion;
using UnityEngine;

namespace Sumo
{
    public enum SumoInputButton
    {
        Brake = 0
    }

    public struct SumoInputData : INetworkInput
    {
        public Vector2 Move;
        public float CameraYaw;
        public NetworkButtons Buttons;
    }
}

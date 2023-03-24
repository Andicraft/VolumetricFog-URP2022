using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Andicraft.VolumetricFog
{
    [ExecuteAlways]
    public class FogDensityVolume : MonoBehaviour
    {
        // Strictly a data carrying class
        [Min(0)]
        public float densityMultiplier = 1.0f;
        public bool overrideColor = false;
        public Color color = Color.white;
        [Range(0, 1)]
        public float fade = 0.5f;

        [System.NonSerialized]
        public FogVolumeBufferStruct bufferStruct;

        private bool registered = false;

        private void Awake()
        {
            bufferStruct = new FogVolumeBufferStruct();
            UpdateStructValues();
            RegisterVolume();
        }

        private void Update()
        {
            UpdateStructValues();
        }

        private void OnEnable()
        {
            RegisterVolume();
        }

        private void OnDisable()
        {
            UnregisterVolume();
        }

        private void OnDestroy()
        {
            UnregisterVolume();
        }

        void UpdateStructValues()
        {
            bufferStruct.radius = transform.localScale.magnitude;
            bufferStruct.density = densityMultiplier;
            bufferStruct.position = transform.position;
            bufferStruct.color = new Vector3(color.r, color.g, color.b);
            bufferStruct.fade = fade;
        }

        void RegisterVolume()
        {
            if (Application.isPlaying)
            {
                if (registered == false && VolumetricFog.instance)
                {
                    VolumetricFog.instance.AddVolume(this);
                    registered = true;
                }
            }
            else
            {
                VolumetricFog f = FindObjectOfType<VolumetricFog>();
                if (registered == false && f != null)
                {
                    f.AddVolume(this);
                    registered = true;
                }
            }
        }

        void UnregisterVolume()
        {
            if (Application.isPlaying)
            {
                if (registered == true && VolumetricFog.instance)
                {
                    VolumetricFog.instance.RemoveVolume(this);
                    registered = false;
                }
                else if (registered == true && VolumetricFog.instance == null)
                {
                    registered = false;
                }
            }
            else
            {
                VolumetricFog f = FindObjectOfType<VolumetricFog>();
                if (registered == true && f != null)
                {
                    f.RemoveVolume(this);
                    registered = false;
                }
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(transform.position, transform.localScale.magnitude);
        }

        public struct FogVolumeBufferStruct
        {
            public Vector3 position;
            public float radius;
            public float density;
            public Vector3 color;
            public float fade;
        }

        public readonly static int BufferStride =
            sizeof(float) * 3 +     // Position - 12
            sizeof(float) +         // Radius - 4
            sizeof(float) +         // Density - 4
            sizeof(float) * 3 +     // Color - 12
            sizeof(float);          // Fade - 4
    }
}

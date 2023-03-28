using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;

namespace Andicraft.VolumetricFog
{
    [ExecuteAlways]
    public class VolumetricFog : MonoBehaviour
    {
        [Min(0)]
        public float density = 1.0f;
        public Color fogColor = Color.white;
        [Min(50)]
        public float skyDistance = 500;
        [Range(0, 1)]
        public float scatteringCoefficient = 1;
        [Range(0, 1)]
        public float extinctionCoefficient = 1;
        public Texture2D blueNoise;
        [Range(0, 0.95f)]
        public float anisotropy = 0.85f;
        public bool heightFog;
        [Min(0)]
        public float heightFogDensity = 3.0f;
        public float heightBase = -10;
        [Min(0.1f)]
        public float heightTransitionSize = 50f;
        [Header("Noise")]
        public Texture3D noiseTexture;
        public Vector4 noiseWeights = Vector4.one;
        public float noiseSize = 50f;
        public Vector3 noiseScrollSpeed = Vector3.down * 0.025f;
        [MinMaxSlider(Bound = true, Min = 0, Max = 1, DataFields = false)]
        public Vector2 minMaxNoiseValues = new Vector2(0.6f, 1);
        [Space(16)]
        public Texture3D curlNoise;
        public float curlSize = 50f;
        public float curlStrength = 0.01f;
        public Vector3 curlScrollSpeed = Vector3.up * 0.015f;
        [Header("Shadowing")]
        [Range(0, 1)]
        public float directionalShadowExtinction;
        [Min(1)]
        public float directionalShadowDensity;
        [Range(0, 5)]
        public float directionalShadowDistance;
        [Range(0, 1)]
        public float additionalShadowExtinction;
        [Min(1)]
        public float additionalShadowDensity;
        [Range(0, 5)]
        public float additionalShadowDistance;

        public List<FogDensityVolume> volumes { get; private set; }
        public static VolumetricFog instance { get; private set; }

        private GlobalKeyword heightFogKeyword;

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(this);
            }
            else
            {
                instance = this;
                volumes ??= new();
            }
        }

        void OnEnable()
        {
            Shader.SetGlobalInt(s_VFogEnabled, 1);
            heightFogKeyword = UnityEngine.Rendering.GlobalKeyword.Create("_VFOG_HEIGHTFOG");
            UpdateValues();
        }

        void OnDisable()
        {
            Shader.SetGlobalInt(s_VFogEnabled, 0);
        }


        void OnValidate()
        {
            UpdateValues();
        }

        void Update()
        {
            Shader.SetGlobalTexture(s_VFogBlueNoise, blueNoise);
        }

        void UpdateValues()
        {
            Shader.SetGlobalFloat(s_VFogDensity, density);
            Shader.SetGlobalColor(s_VFogColor, fogColor);
            Shader.SetGlobalFloat(s_VFogScattering, scatteringCoefficient);
            Shader.SetGlobalFloat(s_VFogExtinction, extinctionCoefficient);
            Shader.SetGlobalFloat(s_VFogAnisotropy, anisotropy);
            Shader.SetGlobalFloat(s_SkyDistance, skyDistance);
            Shader.SetGlobalFloat(s_HeightFogDensity, heightFogDensity);
            Shader.SetGlobalFloat(s_HeightBase, heightBase);
            Shader.SetGlobalFloat(s_HeightTransitionSize, heightTransitionSize);
            Shader.SetGlobalTexture(s_NoiseTexture, noiseTexture);
            Shader.SetGlobalFloat(s_NoiseSize, noiseSize);
            Shader.SetGlobalVector(s_NoiseWeights, noiseWeights);
            Shader.SetGlobalVector(s_NoiseScrollSpeed, noiseScrollSpeed);
            Shader.SetGlobalVector(s_MinMaxNoise, minMaxNoiseValues);
            Shader.SetGlobalTexture(s_CurlTexture, curlNoise);
            Shader.SetGlobalFloat(s_CurlSize, curlSize);
            Shader.SetGlobalFloat(s_CurlStrength, curlStrength);
            Shader.SetGlobalVector(s_CurlSpeed, curlScrollSpeed);
            Shader.SetGlobalFloat(s_AdditionalShadowDensity, additionalShadowDensity);
            Shader.SetGlobalFloat(s_AdditionalShadowExtinction, additionalShadowExtinction);
            Shader.SetGlobalFloat(s_DirectionalShadowDensity, directionalShadowDensity);
            Shader.SetGlobalFloat(s_DirectionalShadowExtinction, directionalShadowExtinction);
            Shader.SetGlobalFloat(s_DirectionalShadowDistance, directionalShadowDistance);
            Shader.SetGlobalFloat(s_AdditionalShadowDistance, additionalShadowDistance);

            Shader.SetKeyword(heightFogKeyword, heightFog);
        }

        public static readonly int s_VFogEnabled = Shader.PropertyToID("_VFogEnabled");
        public static readonly int s_VFogDensity = Shader.PropertyToID("_VFogDensity");
        public static readonly int s_VFogColor = Shader.PropertyToID("_VFogColor");
        public static readonly int s_VFogScattering = Shader.PropertyToID("_VFogScattering");
        public static readonly int s_VFogExtinction = Shader.PropertyToID("_VFogExtinction");
        public static readonly int s_VFogBlueNoise = Shader.PropertyToID("_VFogBlueNoise");
        public static readonly int s_VFogAnisotropy = Shader.PropertyToID("_VFogAnisotropy");
        public static readonly int s_SkyDistance = Shader.PropertyToID("_VFogSkyDistance");
        public static readonly int s_HeightFogDensity = Shader.PropertyToID("_VFogHeightFogDensity");
        public static readonly int s_HeightBase = Shader.PropertyToID("_VFogHeightBase");
        public static readonly int s_HeightTransitionSize = Shader.PropertyToID("_VFogHeightTransitionSize");
        public static readonly int s_NoiseTexture = Shader.PropertyToID("_VFogNoiseTexture");
        public static readonly int s_NoiseSize = Shader.PropertyToID("_VFogNoiseSize");
        public static readonly int s_NoiseWeights = Shader.PropertyToID("_VFogNoiseWeights");
        public static readonly int s_NoiseScrollSpeed = Shader.PropertyToID("_VFogNoiseSpeed");
        public static readonly int s_MinMaxNoise = Shader.PropertyToID("_VFogMinMaxNoise");
        public static readonly int s_CurlTexture = Shader.PropertyToID("_VFogCurlTexture");
        public static readonly int s_CurlSize = Shader.PropertyToID("_VFogCurlSize");
        public static readonly int s_CurlStrength = Shader.PropertyToID("_VFogCurlStrength");
        public static readonly int s_CurlSpeed = Shader.PropertyToID("_VFogCurlSpeed");
        public static readonly int s_DirectionalShadowExtinction = Shader.PropertyToID("_VFogDirShadowExtinction");
        public static readonly int s_DirectionalShadowDensity = Shader.PropertyToID("_VFogDirShadowDensity");
        public static readonly int s_AdditionalShadowExtinction = Shader.PropertyToID("_VFogAddShadowExtinction");
        public static readonly int s_AdditionalShadowDensity = Shader.PropertyToID("_VFogAddShadowDensity");
        public static readonly int s_DirectionalShadowDistance = Shader.PropertyToID("_VFogDirShadowDistance");
        public static readonly int s_AdditionalShadowDistance = Shader.PropertyToID("_VFogAddShadowDistance");

        public void AddVolume(FogDensityVolume v)
        {
            if (v == null) return;
            volumes ??= new();
            volumes.Add(v);
        }

        public void RemoveVolume(FogDensityVolume v)
        {
            if (v == null || volumes == null) return;
            volumes.Remove(v);
        }

    }
}

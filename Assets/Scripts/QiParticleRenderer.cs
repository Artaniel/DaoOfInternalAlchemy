using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Draws the particles owned by <see cref="QiParticleManager"/> with instanced draw calls.
/// Particles are grouped by element, so each group keeps GPU instancing but gets its own color.
/// Initialization and per-frame calls are driven by <see cref="Bootstrap"/>; this component
/// has no lifecycle callbacks of its own.
/// </summary>
[DisallowMultipleComponent]
public sealed class QiParticleRenderer : MonoBehaviour
{
    // RenderMeshInstanced accepts up to 1023 instances per call, but the URP Unlit shader
    // does not use "instancing_options assumeuniformscaling", so each instance also carries
    // a worldToObject matrix and the practical limit is 511.
    const int MaxInstancesPerBatch = 511;

    [SerializeField] GameObject _particlePrefab;
    [SerializeField] float _particleSize = 0.15f;
    [Tooltip("Цвета стихий: 0 дерево, 1 огонь, 2 земля, 3 металл, 4 вода.")]
    [SerializeField] Color[] _elementColors =
    {
        new Color(0.25f, 0.85f, 0.35f),
        new Color(1f, 0.35f, 0.12f),
        new Color(0.85f, 0.62f, 0.22f),
        new Color(0.9f, 0.92f, 0.98f),
        new Color(0.15f, 0.5f, 1f),
    };

    QiParticleManager _particleManager;
    Mesh _mesh;
    Material _material;
    Material[] _elementMaterials;
    NativeArray<Matrix4x4> _matrices;
    RenderParams _renderParams;

    int[] _elementCounts;
    int[] _elementStarts;
    int[] _elementCursors;

    public void Initialize(QiParticleManager particleManager)
    {
        _particleManager = particleManager;

        MeshFilter meshFilter = _particlePrefab.GetComponentInChildren<MeshFilter>();
        MeshRenderer meshRenderer = _particlePrefab.GetComponentInChildren<MeshRenderer>();

        _mesh = meshFilter.sharedMesh;
        _material = meshRenderer.sharedMaterial;

        _matrices = new NativeArray<Matrix4x4>(
            _particleManager.Capacity,
            Allocator.Persistent,
            NativeArrayOptions.UninitializedMemory);

        _renderParams = new RenderParams(_material)
        {
            worldBounds = new Bounds(Vector3.zero, new Vector3(QiFieldGrid.WorldSize, QiFieldGrid.WorldSize, 1f)),
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
            layer = 0
        };

        EnsureElementColors();

        // URP Unlit keeps _BaseColor outside the instancing buffer, so a property block
        // would not reach an instanced draw. One material copy per element keeps the
        // per-element color reliable while each element is still drawn instanced.
        _elementMaterials = new Material[QiParticleData.ElementCount];
        for (int element = 0; element < QiParticleData.ElementCount; element++)
        {
            Material material = new Material(_material)
            {
                name = $"{_material.name} ({element})",
                hideFlags = HideFlags.DontSave,
            };
            material.enableInstancing = true;
            _elementMaterials[element] = material;
        }

        _elementCounts = new int[QiParticleData.ElementCount];
        _elementStarts = new int[QiParticleData.ElementCount];
        _elementCursors = new int[QiParticleData.ElementCount];
    }

    public void Render()
    {
        NativeArray<QiParticleData> particles = _particleManager.Particles;
        int count = Mathf.Min(_particleManager.ActiveCount, particles.Length);
        float size = Mathf.Max(0.001f, _particleSize);

        Array.Clear(_elementCounts, 0, _elementCounts.Length);

        for (int i = 0; i < count; i++)
        {
            int element = particles[i].Element % QiParticleData.ElementCount;
            _elementCounts[element]++;
        }

        int running = 0;
        for (int element = 0; element < QiParticleData.ElementCount; element++)
        {
            _elementStarts[element] = running;
            _elementCursors[element] = running;
            running += _elementCounts[element];
        }

        for (int i = 0; i < count; i++)
        {
            QiParticleData particle = particles[i];
            int element = particle.Element % QiParticleData.ElementCount;
            int index = _elementCursors[element]++;

            _matrices[index] = Matrix4x4.TRS(
                new Vector3(particle.Position.x, particle.Position.y, 0f),
                Quaternion.identity,
                Vector3.one * size);
        }

        for (int element = 0; element < QiParticleData.ElementCount; element++)
        {
            int elementCount = _elementCounts[element];
            if (elementCount == 0)
            {
                continue;
            }

            _elementMaterials[element].SetColor("_BaseColor", _elementColors[element]);
            _renderParams.material = _elementMaterials[element];

            int end = _elementStarts[element] + elementCount;
            for (int start = _elementStarts[element]; start < end; start += MaxInstancesPerBatch)
            {
                int batchCount = Mathf.Min(MaxInstancesPerBatch, end - start);
                Graphics.RenderMeshInstanced(_renderParams, _mesh, 0, _matrices, batchCount, start);
            }
        }
    }

    void EnsureElementColors()
    {
        if (_elementColors != null && _elementColors.Length == QiParticleData.ElementCount)
        {
            return;
        }

        _elementColors = new[]
        {
            new Color(0.25f, 0.85f, 0.35f),
            new Color(1f, 0.35f, 0.12f),
            new Color(0.85f, 0.62f, 0.22f),
            new Color(0.9f, 0.92f, 0.98f),
            new Color(0.15f, 0.5f, 1f),
        };
    }

    void OnDestroy()
    {
        if (_elementMaterials != null)
        {
            for (int element = 0; element < _elementMaterials.Length; element++)
            {
                if (_elementMaterials[element] != null)
                {
                    Destroy(_elementMaterials[element]);
                }
            }
        }

        if (_matrices.IsCreated)
        {
            _matrices.Dispose();
        }
    }
}

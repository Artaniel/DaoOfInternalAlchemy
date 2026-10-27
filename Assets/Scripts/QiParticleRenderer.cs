using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Draws the particles owned by <see cref="QiParticleManager"/> with a single instanced draw call.
/// Initialization and per-frame calls are driven by <see cref="Bootstrap"/>; this component has no lifecycle callbacks of its own.
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

    QiParticleManager _particleManager;
    Mesh _mesh;
    Material _material;
    NativeArray<Matrix4x4> _matrices;
    RenderParams _renderParams;

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
    }

    public void Render()
    {
        NativeArray<QiParticleData> particles = _particleManager.Particles;
        int count = Mathf.Min(_particleManager.ActiveCount, particles.Length);
        float size = Mathf.Max(0.001f, _particleSize);

        for (int i = 0; i < count; i++)
        {
            QiParticleData particle = particles[i];
            _matrices[i] = Matrix4x4.TRS(
                new Vector3(particle.Position.x, particle.Position.y, 0f),
                Quaternion.identity,
                Vector3.one * size);
        }

        for (int start = 0; start < count; start += MaxInstancesPerBatch)
        {
            int batchCount = Mathf.Min(MaxInstancesPerBatch, count - start);
            Graphics.RenderMeshInstanced(_renderParams, _mesh, 0, _matrices, batchCount, start);
        }
    }

    void OnDestroy()
    {
        if (_matrices.IsCreated)
        {
            _matrices.Dispose();
        }
    }
}

using Unity.Collections;
using UnityEngine;

public sealed class QiParticleManager : MonoBehaviour
{
    public const int ParticleCapacity = 100;

    readonly QiFieldGrid _field = new();

    NativeArray<QiParticleData> _particles;

    public bool IsInitialized { get; private set; }

    public QiFieldGrid Field => _field;
    public NativeArray<QiParticleData> Particles => _particles;
    public int ActiveCount;

    public void Initialize()
    {
        if (IsInitialized)
        {
            return;
        }

        _field.Allocate();
        _particles = new NativeArray<QiParticleData>(ParticleCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        IsInitialized = true;

        SpawnInitialParticles();

        Debug.Log(
            $"[QiParticleManager] Initialized field {QiFieldGrid.Width}x{QiFieldGrid.Height} ({QiFieldGrid.CellCount} cells) and {_particles.Length} particles.");
    }

    internal void Spawn(int index, float x, float y, byte element, float charge)
    {
        _particles[index] = new QiParticleData { Position = new float2(x, y), Velocity = new float2(0f, 0f), Element = element, Charge = charge, Mass = 1f };
        ActiveCount++;
    }

    internal void SpawnInitialParticles()
    {
        for (int i = 0; i < ParticleCapacity / 4; i++)
        {
            float angle = Random.value * 2 * Mathf.PI;
            float radius = 15f + Random.value * 15f;
            Spawn(i, Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, Random.value > 0.5f ? 1 : 0);
        }
    }

    void OnDestroy()
    {
        if (_particles.IsCreated)
        {
            _particles.Dispose();
        }

        _field.Dispose();
        IsInitialized = false;
    }
}
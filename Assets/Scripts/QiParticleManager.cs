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

    public void Initialize()
    {
        if (IsInitialized)
        {
            return;
        }

        _field.Allocate();
        _particles = new NativeArray<QiParticleData>(ParticleCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        IsInitialized = true;

        Debug.Log(
            $"[QiParticleManager] Initialized field {QiFieldGrid.Width}x{QiFieldGrid.Height} ({QiFieldGrid.CellCount} cells) and {_particles.Length} particles.");
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

using Unity.Collections;
using UnityEngine;
using Unity.Mathematics;

public sealed class QiParticleManager : MonoBehaviour
{
    [SerializeField] int _particleCapacity = 100;
    [SerializeField] float _speedMin = 0.25f;
    [SerializeField] float _speedMax = 1f;

    readonly QiFieldGrid _field = new();

    NativeArray<QiParticleData> _particles;

    public bool IsInitialized { get; private set; }

    public int Capacity => _particleCapacity;
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
        _particles = new NativeArray<QiParticleData>(_particleCapacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        IsInitialized = true;

        ActiveCount = 0;
        SpawnInitialParticles();

        Debug.Log(
            $"[QiParticleManager] Initialized field {QiFieldGrid.Width}x{QiFieldGrid.Height} ({QiFieldGrid.CellCount} cells) and {_particles.Length} particles.");
    }

    public void Tick(float deltaTime)
    {
        for (int i = 0; i < ActiveCount; i++)
        {
            QiParticleData particle = _particles[i];
            particle.Position += particle.Velocity * deltaTime;
            particle.Position = new float2(WrapCoordinate(particle.Position.x), WrapCoordinate(particle.Position.y));
            _particles[i] = particle;
        }
    }

    internal void Spawn(int index, float x, float y, byte element, float charge)
    {
        _particles[index] = new QiParticleData
        {
            Position = new float2(x, y),
            Velocity = RandomVelocity(),
            Element = element,
            Charge = charge,
            Mass = 1f,
        };

        ActiveCount++;
    }

    internal void SpawnInitialParticles()
    {
        for (int i = 0; i < _particleCapacity; i++)
        {
            float angle = UnityEngine.Random.value * 2 * Mathf.PI;
            float radius = QiFieldGrid.WorldHalfSize * (0.3f + UnityEngine.Random.value * 0.4f);
            Spawn(
                i, 
                Mathf.Cos(angle) * radius, 
                Mathf.Sin(angle) * radius, (byte)(UnityEngine.Random.value > 0.5f ? 1 : 0), 1);
        }
    }

    float2 RandomVelocity()
    {
        float angle = UnityEngine.Random.value * 2f * Mathf.PI;
        float speed = _speedMin + UnityEngine.Random.value * (_speedMax - _speedMin);

        return new float2(Mathf.Cos(angle) * speed, Mathf.Sin(angle) * speed);
    }

    static float WrapCoordinate(float value)
    {
        float wrapped = (value + QiFieldGrid.WorldHalfSize) % QiFieldGrid.WorldSize;
        if (wrapped < 0f)
        {
            wrapped += QiFieldGrid.WorldSize;
        }

        return wrapped - QiFieldGrid.WorldHalfSize;
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
using Unity.Collections;
using UnityEngine;
using Unity.Mathematics;

public sealed class QiParticleManager : MonoBehaviour
{
    [Tooltip("Максимальный модуль скорости, world units/s. Направление сохраняется.")]
    [SerializeField] float _speedLimit = 2f;
    [Tooltip("Радиус штампа в клетках: окно (2r+1)². При клетке 0.01 и r=30 это 0.3 world units.")]
    [SerializeField] int _stampRadius = 30;
    [Tooltip("k в Φ = k·q / r².")]
    [SerializeField] float _potentialScale = 0.002f;
    [Tooltip("1/m: множитель от градиента потенциала к ускорению.")]
    [SerializeField] float _accelerationScale = 1f;

    readonly QiFieldGrid _field = new();

    NativeArray<QiParticleData> _particles;

    public bool IsInitialized { get; private set; }

    public int Capacity => _particles.Length;
    public QiFieldGrid Field => _field;
    public NativeArray<QiParticleData> Particles => _particles;
    public int ActiveCount;

    public void Initialize(int capacity)
    {
        if (IsInitialized)
        {
            return;
        }

        _field.Allocate();
        _particles = new NativeArray<QiParticleData>(capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        IsInitialized = true;

        ActiveCount = 0;

        Debug.Log(
            $"[QiParticleManager] Initialized field {QiFieldGrid.Width}x{QiFieldGrid.Height} ({QiFieldGrid.CellCount} cells) and {capacity} particles.");
    }

    public void Tick(float deltaTime)
    {
        _field.Clear();
        StampField();
        Integrate(deltaTime);
    }

    /// <summary>
    /// Each particle adds Φ = k·q / r² to the cells around its position.
    /// r is the distance between cell centers offset by half a cell, so the expression never divides by zero.
    /// The stamp is centered on the particle's cell and the kernel depends only on |Δ|, so the ±1 cell
    /// contributions are equal and cancel in the central difference used to compute the force.
    /// </summary>
    void StampField()
    {
        float halfCell = QiFieldGrid.CellSize * 0.5f;

        for (int i = 0; i < ActiveCount; i++)
        {
            QiParticleData particle = _particles[i];
            float2 gridPosition = QiFieldGrid.WorldToGrid(particle.Position);
            int centerX = (int)math.floor(gridPosition.x);
            int centerY = (int)math.floor(gridPosition.y);
            float strength = _potentialScale * particle.Charge;

            for (int offsetY = -_stampRadius; offsetY <= _stampRadius; offsetY++)
            {
                int cellY = WrapIndex(centerY + offsetY, QiFieldGrid.Height);
                float worldY = offsetY * QiFieldGrid.CellSize;

                for (int offsetX = -_stampRadius; offsetX <= _stampRadius; offsetX++)
                {
                    int cellX = WrapIndex(centerX + offsetX, QiFieldGrid.Width);
                    float worldX = offsetX * QiFieldGrid.CellSize;
                    float distance = math.sqrt(worldX * worldX + worldY * worldY) + halfCell;

                    _field.Add(cellX, cellY, strength / (distance * distance));
                }
            }
        }
    }

    /// <summary>
    /// E = −∇Φ from the central difference of neighbour cells, F = E·q, a = F/m.
    /// The minus sign gives repulsion between like charges; <see cref="_accelerationScale"/> acts as 1/m.
    /// Velocity is clamped to <see cref="_speedLimit"/> with its direction preserved.
    /// </summary>
    void Integrate(float deltaTime)
    {
        float inverseCellSpan = 1f / (2f * QiFieldGrid.CellSize);

        for (int i = 0; i < ActiveCount; i++)
        {
            QiParticleData particle = _particles[i];
            float2 gridPosition = QiFieldGrid.WorldToGrid(particle.Position);
            int cellX = (int)math.floor(gridPosition.x);
            int cellY = (int)math.floor(gridPosition.y);

            float potentialLeft = _field.Get(WrapIndex(cellX - 1, QiFieldGrid.Width), cellY);
            float potentialRight = _field.Get(WrapIndex(cellX + 1, QiFieldGrid.Width), cellY);
            float potentialDown = _field.Get(cellX, WrapIndex(cellY - 1, QiFieldGrid.Height));
            float potentialUp = _field.Get(cellX, WrapIndex(cellY + 1, QiFieldGrid.Height));

            float2 acceleration = new float2(
                (potentialLeft - potentialRight) * inverseCellSpan,
                (potentialDown - potentialUp) * inverseCellSpan);

            acceleration *= particle.Charge * _accelerationScale;

            particle.Velocity += acceleration * deltaTime;
            particle.Velocity = LimitSpeed(particle.Velocity);
            particle.Position += particle.Velocity * deltaTime;
            particle.Position = new float2(WrapCoordinate(particle.Position.x), WrapCoordinate(particle.Position.y));

            _particles[i] = particle;
        }
    }

    internal void Spawn(int index, float2 position, float2 velocity, byte element, float charge)
    {
        _particles[index] = new QiParticleData
        {
            Position = position,
            Velocity = velocity,
            Element = element,
            Charge = charge,
            Mass = 1f,
        };

        ActiveCount++;
    }

    float2 LimitSpeed(float2 velocity)
    {
        float speedSquared = math.lengthsq(velocity);
        if (speedSquared <= _speedLimit * _speedLimit)
        {
            return velocity;
        }

        return velocity * (_speedLimit / math.sqrt(speedSquared));
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

    static int WrapIndex(int value, int size)
    {
        int wrapped = value % size;
        return wrapped < 0 ? wrapped + size : wrapped;
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

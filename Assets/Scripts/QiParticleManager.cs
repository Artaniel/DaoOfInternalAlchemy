using Unity.Collections;
using UnityEngine;
using Unity.Mathematics;

public sealed class QiParticleManager : MonoBehaviour
{
    [Tooltip("Максимальный модуль скорости, world units/s. Направление сохраняется.")]
    [SerializeField] float _speedLimit = 2f;
    [Tooltip("Множитель плотности заряда для вычисления силы")]
    [SerializeField] float _chargeDensityScale = 1f;
    [Tooltip("1/m: множитель от градиента потенциала к ускорению.")]
    [SerializeField] float _accelerationScale = 1f;
    [Tooltip("Сопротивление среды. Экспоненциальное затухание скорости.")]
    [SerializeField] float _drag = 0.4f;
    [Tooltip("Сила влияния течения на частицы.")]
    [SerializeField] float _flowInfluence = 1f;

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

    public void SetGridSize(int width, int height)
    {
        QiFieldGrid.Configure(width, height);
        if (IsInitialized && _field.IsCreated)
        {
            _field.Dispose();
            _field.Allocate();
        }
    }

    public void Tick(float deltaTime)
    {
        if (!IsInitialized || !_particles.IsCreated)
        {
            return;
        }

        _field.Clear();

        // Build charge density from drops only
        for (int i = 0; i < ActiveCount; i++)
        {
            var p = _particles[i];
            if (p.Type != 1)
            {
                continue;
            }

            float2 gridPosition = QiFieldGrid.WorldToGrid(p.Position);
            int ix = (int)math.floor(gridPosition.x);
            int iy = (int)math.floor(gridPosition.y);

            if (ix < 0 || ix >= QiFieldGrid.Width || iy < 0 || iy >= QiFieldGrid.Height)
            {
                continue;
            }

            _field.Add(ix, iy, 1f);
        }

        // Normalize density
        if (math.abs(_chargeDensityScale) > 1e-12f)
        {
            var cellsArr = _field.Cells;
            for (int i = 0; i < cellsArr.Length; i++)
            {
                cellsArr[i] *= _chargeDensityScale;
            }
        }

        // Integrate all particles by gradient of density field
        float invCell = 1f / QiFieldGrid.CellSize;
        float dragFactor = math.exp(-_drag * deltaTime);

        for (int i = 0; i < ActiveCount; i++)
        {
            QiParticleData particle = _particles[i];

            float2 gridPosition = QiFieldGrid.WorldToGrid(particle.Position);
            float gx = gridPosition.x;
            float gy = gridPosition.y;

            int x0 = (int)math.floor(gx);
            int y0 = (int)math.floor(gy);
            int x1 = x0 + 1;
            int y1 = y0 + 1;

            int wx0 = WrapIndex(x0, QiFieldGrid.Width);
            int wx1 = WrapIndex(x1, QiFieldGrid.Width);
            int wy0 = WrapIndex(y0, QiFieldGrid.Height);
            int wy1 = WrapIndex(y1, QiFieldGrid.Height);

            int wxl = WrapIndex(x0 - 1, QiFieldGrid.Width);
            int wxr = WrapIndex(x1, QiFieldGrid.Width);
            int wyb = WrapIndex(y0 - 1, QiFieldGrid.Height);
            int wyt = WrapIndex(y1, QiFieldGrid.Height);

            // Bilinear potential for reference
            float fx = gx - x0;
            float fy = gy - y0;
            float v00 = _field.Get(wx0, wy0);
            float v10 = _field.Get(wx1, wy0);
            float v01 = _field.Get(wx0, wy1);
            float v11 = _field.Get(wx1, wy1);
            float v0 = math.lerp(v00, v10, fx);
            float v1 = math.lerp(v01, v11, fx);
            _ = math.lerp(v0, v1, fy);

            // Central difference gradient
            float phiLeft = _field.Get(wxl, wy0);
            float phiRight = _field.Get(wxr, wy0);
            float phiBottom = _field.Get(wx0, wyb);
            float phiTop = _field.Get(wx0, wyt);

            float2 grad = new float2((phiRight - phiLeft) * 0.5f * invCell, (phiTop - phiBottom) * 0.5f * invCell);

            if (particle.Type == 1)
            {
                // Drops also feel drag, sources only contribute to field
                particle.Velocity *= dragFactor;
                particle.Position += particle.Velocity * deltaTime;
                particle.Position = new float2(WrapCoordinate(particle.Position.x), WrapCoordinate(particle.Position.y));
                _particles[i] = particle;
                continue;
            }

            // Sparks follow gradient: F = -∇Φ
            float2 force = -grad * _accelerationScale * _flowInfluence;
            particle.Velocity += force * deltaTime;
            particle.Velocity *= dragFactor;
            particle.Velocity = LimitSpeed(particle.Velocity);
            particle.Position += particle.Velocity * deltaTime;
            particle.Position = new float2(WrapCoordinate(particle.Position.x), WrapCoordinate(particle.Position.y));

            _particles[i] = particle;
        }
    }

    internal void Spawn(int index, float2 position, float2 velocity, byte element, byte type, float charge)
    {
        _particles[index] = new QiParticleData
        {
            Position = position,
            Velocity = velocity,
            Element = element,
            Type = type,
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

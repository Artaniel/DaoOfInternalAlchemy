using Unity.Collections;
using UnityEngine;
using Unity.Mathematics;

/// <summary>
/// Owns the particles and advances the prototype simulation one frame.
/// Each element has its own density field; particles deposit into their own field
/// and feel the gradients of all other fields through an asymmetric matrix.
/// A sixth all-particle field adds a short-range pressure, so dense clumps push apart.
/// </summary>
public sealed class QiParticleManager : MonoBehaviour
{
    [Tooltip("Максимальный модуль скорости, world units/s. Направление сохраняется.")]
    [SerializeField] float _speedLimit = 2f;
    [Tooltip("1/m: множитель градиента плотности в ускорение.")]
    [SerializeField] float _accelerationScale = 1f;
    [Tooltip("Опорная плотность, на которую делится градиент. Держит силу независимой от числа частиц.")]
    [SerializeField] float _densityReference = 1f;
    [Tooltip("Сигма гауссова размытия плотности, в клетках.")]
    [SerializeField] float _blurSigmaCells = 2f;
    [Tooltip("Сила короткодействующего давления. 0 отключает его.")]
    [SerializeField] float _pressureStrength = 1f;
    [Tooltip("Сигма поля давления, в клетках. Держать малой: 0.5–1.")]
    [SerializeField] float _pressureSigmaCells = 0.75f;
    [Tooltip("Опорная плотность давления, частиц на клетку. Делит градиент, чтобы сила не зависела от общего числа частиц.")]
    [SerializeField] float _pressureReferenceDensity = 10f;
    [Tooltip("Матрица A[читающая стихия][поле источника] (0 дерево, 1 огонь, 2 земля, 3 металл, 4 вода): плюс — притяжение, минус — отталкивание.")]
    [SerializeField] float[] _interaction = new float[QiParticleData.ElementCount * QiParticleData.ElementCount];
    [Tooltip("Сопротивление среды. Экспоненциальное затухание скорости.")]
    [SerializeField] float _drag = 0.4f;
    [Tooltip("Скорость релаксации скорости частицы к течению.")]
    [SerializeField] float _flowInfluence = 1f;
    [Tooltip("Базовая конвекция котла, world units/s у центра.")]
    [SerializeField] float _convectionStrength = 0.25f;
    [Tooltip("Множитель скорости следа мыши.")]
    [SerializeField] float _pointerFlowStrength = 0.2f;
    [Tooltip("Затухание следа мыши, 1/s.")]
    [SerializeField] float _pointerFlowDecay = 1.5f;
    [Tooltip("Сигма пятна следа мыши, в клетках.")]
    [SerializeField] float _pointerFlowSigmaCells = 2f;

    readonly QiFieldGrid _field = new();
    readonly QiVectorField _pointerFlow = new();

    NativeArray<QiParticleData> _particles;

    float2 _lastPointer;
    bool _pointerWasDown;
    bool _hasLastPointer;

    public bool IsInitialized { get; private set; }

    public int Capacity => _particles.Length;
    public QiFieldGrid Field => _field;
    public NativeArray<QiParticleData> Particles => _particles;
    public int ActiveCount;

    public void Initialize(int capacity)
    {
        // After a domain reload during Play the flag can survive while native arrays do not,
        // so re-initialize when the particle buffer is missing.
        if (IsInitialized && _particles.IsCreated)
        {
            return;
        }

        _field.Allocate();
        _pointerFlow.Allocate();
        EnsureInteraction();

        _particles = new NativeArray<QiParticleData>(capacity, Allocator.Persistent, NativeArrayOptions.ClearMemory);
        IsInitialized = true;

        ActiveCount = 0;

        Debug.Log(
            $"[QiParticleManager] Initialized {QiParticleData.ElementCount} density fields {QiFieldGrid.Width}x{QiFieldGrid.Height} ({QiFieldGrid.CellCount} cells) and {capacity} particles.");
    }

    public void SetGridSize(int width, int height)
    {
        QiFieldGrid.Configure(width, height);
        if (IsInitialized && _field.IsCreated)
        {
            _field.Dispose();
            _field.Allocate();
            _pointerFlow.Dispose();
            _pointerFlow.Allocate();
        }
    }

    public void Tick(float deltaTime, float2 pointerWorld, bool pointerDown)
    {
        if (!IsInitialized || !_particles.IsCreated)
        {
            return;
        }

        float delta = math.min(deltaTime, 0.05f);
        if (delta <= 0f)
        {
            return;
        }

        BuildFields();
        UpdatePointerFlow(pointerWorld, pointerDown, delta);

        float invReferenceDensity = 1f / math.max(_densityReference, 1e-4f);
        float pressureScale = _pressureStrength > 0f
            ? _pressureStrength / math.max(_pressureReferenceDensity, 1e-4f)
            : 0f;
        float dragFactor = math.exp(-_drag * delta);
        float flowBlend = 1f - math.exp(-_flowInfluence * delta);

        for (int i = 0; i < ActiveCount; i++)
        {
            QiParticleData particle = _particles[i];
            int element = particle.Element % QiParticleData.ElementCount;

            float2 acceleration = float2.zero;
            int row = element * QiParticleData.ElementCount;

            for (int source = 0; source < QiParticleData.ElementCount; source++)
            {
                if (source == element)
                {
                    continue;
                }

                float weight = _interaction[row + source];
                if (math.abs(weight) < 1e-6f)
                {
                    continue;
                }

                acceleration += _field.SampleGradient(source, particle.Position) * weight;
            }

            particle.Velocity += acceleration * (_accelerationScale * invReferenceDensity) * delta;

            if (pressureScale > 0f)
            {
                float2 pressureGradient = _field.SampleGradient(QiFieldGrid.PressureField, particle.Position);
                particle.Velocity -= pressureGradient * (pressureScale * delta);
            }

            float2 flow = BaseConvection(particle.Position) + _pointerFlow.Sample(particle.Position);
            particle.Velocity += (flow - particle.Velocity) * flowBlend;

            particle.Velocity *= dragFactor;
            particle.Velocity = LimitSpeed(particle.Velocity);
            particle.Position += particle.Velocity * delta;
            particle.Position = new float2(WrapCoordinate(particle.Position.x), WrapCoordinate(particle.Position.y));

            _particles[i] = particle;
        }
    }

    void BuildFields()
    {
        _field.Clear();

        for (int i = 0; i < ActiveCount; i++)
        {
            QiParticleData particle = _particles[i];
            _field.Deposit(particle.Element % QiParticleData.ElementCount, particle.Position, 1f);
        }

        _field.BuildPressureField();
        _field.Blur(_blurSigmaCells);
        _field.BlurPressure(_pressureSigmaCells);
        _field.ComputeGradients();
    }

    void UpdatePointerFlow(float2 pointerWorld, bool pointerDown, float delta)
    {
        _pointerFlow.Decay(_pointerFlowDecay, delta);

        if (!pointerDown)
        {
            _hasLastPointer = false;
            _pointerWasDown = false;
            return;
        }

        if (_pointerWasDown && _hasLastPointer)
        {
            float2 pointerVelocity = (pointerWorld - _lastPointer) / delta;
            float speed = math.length(pointerVelocity);
            float maxPointerSpeed = _speedLimit * 4f;

            if (speed > maxPointerSpeed)
            {
                pointerVelocity *= maxPointerSpeed / speed;
                speed = maxPointerSpeed;
            }

            if (speed > 1e-4f)
            {
                _pointerFlow.Stamp(
                    pointerWorld,
                    pointerVelocity * _pointerFlowStrength,
                    _pointerFlowSigmaCells);
            }
        }

        _lastPointer = pointerWorld;
        _hasLastPointer = true;
        _pointerWasDown = true;
    }

    /// <summary>
    /// Slow swirl with a soft updraft in the middle, roughly like convection in a pot.
    /// Bubble wakes can be added to the flow later.
    /// </summary>
    float2 BaseConvection(float2 position)
    {
        float2 normalized = position / QiFieldGrid.WorldHalfSize;
        float falloff = math.max(0f, 1f - math.dot(normalized, normalized));
        float2 swirl = new float2(-normalized.y, normalized.x) * (_convectionStrength * falloff);
        float2 updraft = new float2(0f, _convectionStrength * 0.4f * falloff);
        return swirl + updraft;
    }

    internal void Spawn(int index, float2 position, float2 velocity, byte element)
    {
        _particles[index] = new QiParticleData
        {
            Position = position,
            Velocity = velocity,
            Element = element,
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

    /// <summary>
    /// Default ring of the five elements. Each element is attracted to the one that
    /// generates it, repelled by the one that destroys it, and weakly repelled by
    /// the one it destroys. The diagonal stays zero: no self-interaction.
    /// </summary>
    void EnsureInteraction()
    {
        int size = QiParticleData.ElementCount * QiParticleData.ElementCount;
        if (_interaction == null || _interaction.Length != size)
        {
            _interaction = new float[size];
        }

        bool hasValue = false;
        for (int i = 0; i < _interaction.Length; i++)
        {
            if (math.abs(_interaction[i]) > 1e-6f)
            {
                hasValue = true;
                break;
            }
        }

        if (hasValue)
        {
            return;
        }

        for (int element = 0; element < QiParticleData.ElementCount; element++)
        {
            int row = element * QiParticleData.ElementCount;
            _interaction[row + (element + 4) % QiParticleData.ElementCount] = 1f;
            _interaction[row + (element + 3) % QiParticleData.ElementCount] = -1f;
            //_interaction[row + (element + 2) % QiParticleData.ElementCount] = -0.5f;
        }
    }

    void OnDestroy()
    {
        if (_particles.IsCreated)
        {
            _particles.Dispose();
        }

        _pointerFlow.Dispose();
        _field.Dispose();
        IsInitialized = false;
    }
}

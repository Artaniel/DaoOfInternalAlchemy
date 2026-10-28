using UnityEngine;
using Unity.Mathematics;

/// <summary>
/// Builds the initial particle cloud. Owns everything about the starting distribution:
/// count, ring radii, speed range and charge. Runs once from <see cref="Bootstrap.Initialize"/>.
/// </summary>
public sealed class QiParticleSpawner : MonoBehaviour
{
    [Tooltip("Сколько частиц создать. Столько же выделит QiParticleManager под буфер.")]
    [SerializeField] int _particleCount = 100;
    [Tooltip("Внутренний радиус кольца спавна, world units.")]
    [SerializeField] float _innerRadius = 1.5f;
    [Tooltip("Внешний радиус кольца спавна, world units.")]
    [SerializeField] float _outerRadius = 3.5f;
    [Tooltip("Минимальный модуль начальной скорости, world units/s.")]
    [SerializeField] float _minSpeed = 0.25f;
    [Tooltip("Максимальный модуль начальной скорости, world units/s.")]
    [SerializeField] float _maxSpeed = 1f;
    [Tooltip("Заряд каждой созданной частицы.")]
    [SerializeField] float _charge = 1f;
    [Tooltip("Доля частиц, которые будут источниками поля (капли). Остальные - искры.")]
    [Range(0f, 1f)]
    [SerializeField] float _dropRatio = 0.01f;

    public int ParticleCount => _particleCount;

    public void Build(QiParticleManager particleManager)
    {
        for (int i = 0; i < _particleCount; i++)
        {
            float angle = UnityEngine.Random.value * 2f * Mathf.PI;
            float radius = Mathf.Lerp(_innerRadius, _outerRadius, UnityEngine.Random.value);
            float2 position = new float2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);

            float direction = UnityEngine.Random.value * 2f * Mathf.PI;
            float speed = Mathf.Lerp(_minSpeed, _maxSpeed, UnityEngine.Random.value);
            float2 velocity = new float2(Mathf.Cos(direction), Mathf.Sin(direction)) * speed;

            byte type = UnityEngine.Random.value < _dropRatio ? (byte)1 : (byte)0;
            particleManager.Spawn(i, position, velocity, 0, type, _charge);
        }
    }
}

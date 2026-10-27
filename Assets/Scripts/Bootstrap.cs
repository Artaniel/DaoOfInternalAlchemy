using UnityEngine;

/// <summary>
/// Single entry point. Owns initialization order and the per-frame order of all services.
/// Services must not implement Awake/Start/Update themselves.
/// </summary>
public sealed class Bootstrap : MonoBehaviour
{
    [SerializeField] QiParticleManager _particleManager;
    [SerializeField] QiParticleRenderer _particleRenderer;

    void Awake()
    {
        Initialize();
    }

    void Update()
    {
        _particleManager.Tick(Time.deltaTime);
        _particleRenderer.Render();
    }

    void Initialize()
    {
        _particleManager.Initialize();
        _particleRenderer.Initialize(_particleManager);

        Debug.Log("[Bootstrap] Application initialized.");
    }
}

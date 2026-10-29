using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Mathematics;

/// <summary>
/// Single entry point. Owns initialization order and the per-frame order of all services.
/// Services must not implement Awake/Start/Update themselves.
/// </summary>
public sealed class Bootstrap : MonoBehaviour
{
    [SerializeField] QiParticleManager _particleManager;
    [SerializeField] QiParticleSpawner _particleSpawner;
    [SerializeField] QiParticleRenderer _particleRenderer;
    [Tooltip("Камера для перевода мыши в мировые координаты. Без неё след мыши отключён.")]
    [SerializeField] Camera _camera;

    void Awake()
    {
        Initialize();
    }

    void Update()
    {
        float2 pointerWorld = default;
        bool pointerDown = false;
        Mouse mouse = Mouse.current;

        if (_camera != null && mouse != null)
        {
            Vector2 screenPosition = mouse.position.ReadValue();
            Vector3 screen = new Vector3(screenPosition.x, screenPosition.y, -_camera.transform.position.z);
            Vector3 world = _camera.ScreenToWorldPoint(screen);
            pointerWorld = new float2(world.x, world.y);
            pointerDown = mouse.leftButton.isPressed;
        }

        _particleManager.Tick(Time.deltaTime, pointerWorld, pointerDown);
        _particleRenderer.Render();
    }

    void Initialize()
    {
        _particleManager.Initialize(_particleSpawner.ParticleCount);
        _particleSpawner.Build(_particleManager);
        _particleRenderer.Initialize(_particleManager);

        Debug.Log("[Bootstrap] Application initialized.");
    }
}

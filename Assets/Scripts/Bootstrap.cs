using UnityEngine;

public class Bootstrap : MonoBehaviour
{
    [SerializeField] QiParticleManager _particleManager;

    void Awake()
    {
        DontDestroyOnLoad(gameObject);

        if (_particleManager == null)
        {
            Debug.LogError("[Bootstrap] QiParticleManager reference is not assigned.");
            return;
        }

        _particleManager.Initialize();
        Debug.Log("[Bootstrap] Application initialized.");
    }
}

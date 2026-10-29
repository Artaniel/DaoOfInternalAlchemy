using Unity.Mathematics;

public struct QiParticleData
{
    // Wood, Fire, Earth, Metal, Water; order follows the generating cycle.
    public const int ElementCount = 5;

    public float2 Position;
    public float2 Velocity;
    public byte Element;
}

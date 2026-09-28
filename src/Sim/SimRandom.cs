namespace Sim;

/// <summary>
/// The simulation's only source of randomness. Hand-rolled xorshift so results don't depend on
/// System.Random's implementation, which can change between .NET versions.
/// </summary>
public sealed class SimRandom(uint seed)
{
    uint _state = seed == 0 ? 1 : seed;

    public uint NextUInt()
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return _state;
    }

    /// <summary>Uniform in [min, max).</summary>
    public float Range(float min, float max) => min + (max - min) * (NextUInt() >> 8) * (1f / (1 << 24));
}

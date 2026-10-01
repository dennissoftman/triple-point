namespace Sim;

/// <summary>
/// The simulation's only source of randomness. Hand-rolled xorshift so results don't depend on
/// System.Random's implementation, which can change between .NET versions. The seed is scrambled first:
/// xorshift's first draws from a small seed (1, 2, ...) are tiny, so without it the first draw of every
/// match leaned the same way.
/// </summary>
public sealed class SimRandom(uint seed)
{
    uint _state = Scramble(seed);

    // A hash finalizer (murmur3's): nearby seeds give unrelated states. Never 0, where xorshift sticks.
    static uint Scramble(uint x)
    {
        x ^= x >> 16; x *= 0x85ebca6b;
        x ^= x >> 13; x *= 0xc2b2ae35;
        x ^= x >> 16;
        return x == 0 ? 1 : x;
    }

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

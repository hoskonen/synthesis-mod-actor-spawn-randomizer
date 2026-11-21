using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;

namespace ModActorSpawnRandomizer;

/// <summary>
/// Seeded selection, byte-for-byte the same construction as the Specters patcher's v1 contract,
/// with this project's own domain string. The decision depends only on the domain, seed,
/// plugin filename (lower-case) and local FormID, never on enumeration order.
/// </summary>
public static class DeterministicSelector
{
    public const string AlgorithmVersion = "v1";
    public const string Domain = "ModActorSpawnRandomizer/v1";

    public static bool IsSelected(FormKey unitKey, int seed, double probability)
        => IsSelected(Domain, unitKey, seed, probability);

    public static ulong ComputeScore(FormKey unitKey, int seed) => ComputeScore(Domain, unitKey, seed);

    public static bool IsSelected(string domain, FormKey unitKey, int seed, double probability)
    {
        ValidateProbability(probability);
        if (probability == 0.0) return false;
        if (probability == 100.0) return true;

        var unitIntervalValue = (ComputeScore(domain, unitKey, seed) >> 11) * (1.0 / 9_007_199_254_740_992.0);
        return unitIntervalValue < probability / 100.0;
    }

    public static ulong ComputeScore(string domain, FormKey unitKey, int seed)
    {
        var domainBytes = Encoding.UTF8.GetBytes(domain);
        var pluginBytes = Encoding.UTF8.GetBytes(unitKey.ModKey.FileName.String.ToLowerInvariant());
        var serialized = new byte[
            sizeof(uint) + domainBytes.Length +
            sizeof(int) +
            sizeof(uint) + pluginBytes.Length +
            sizeof(uint)];

        var offset = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(serialized.AsSpan(offset), (uint)domainBytes.Length);
        offset += sizeof(uint);
        domainBytes.CopyTo(serialized, offset);
        offset += domainBytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(serialized.AsSpan(offset), seed);
        offset += sizeof(int);
        BinaryPrimitives.WriteUInt32LittleEndian(serialized.AsSpan(offset), (uint)pluginBytes.Length);
        offset += sizeof(uint);
        pluginBytes.CopyTo(serialized, offset);
        offset += pluginBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(serialized.AsSpan(offset), unitKey.ID);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(serialized, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    public static void ValidateProbability(double probability)
    {
        if (!double.IsFinite(probability) || probability < 0.0 || probability > 100.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(probability),
                probability,
                "Probability must be a finite value from 0 through 100 inclusive.");
        }
    }
}

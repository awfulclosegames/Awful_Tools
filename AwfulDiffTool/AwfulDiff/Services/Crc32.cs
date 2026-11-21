namespace AwfulDiff.Services;

/// <summary>
/// CRC-32 (zlib / IEEE 802.3): reflected polynomial 0xEDB88320, initial and final XOR 0xFFFFFFFF.
/// The same checksum as zlib's crc32, Python's binascii.crc32, and gzip headers — the empty
/// input hashes to 0x00000000, "123456789" to 0xCBF43926 (the standard self-test vector).
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }
}

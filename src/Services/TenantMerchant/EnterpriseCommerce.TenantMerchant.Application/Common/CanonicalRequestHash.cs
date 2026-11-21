using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EnterpriseCommerce.TenantMerchant.Application.Common;

/// <summary>
/// SHA-256 over a canonical encoding of validated request fields: each field as its UTF-8 byte length (32-bit big
/// endian) followed by its bytes. The first field is a version tag, so that two request kinds never share a hash.
/// </summary>
internal static class CanonicalRequestHash
{
    public static byte[] Compute(params ReadOnlySpan<string> fields)
    {
        using var buffer = new MemoryStream();
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            buffer.Write(length);
            buffer.Write(bytes);
        }

        return SHA256.HashData(buffer.ToArray());
    }
}

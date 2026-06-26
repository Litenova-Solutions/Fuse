using System.Security.Cryptography;
using System.Text;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Fuse.Cli.Rpc;

/// <summary>
///     Generates and validates the host session token issued at process start. The token is returned once in
///     <c>fuse/handshake</c> and required on every other RPC method so casual cross-process callers cannot invoke
///     the host by pipe name alone.
/// </summary>
internal static class FuseHostSessionToken
{
    /// <summary>Generates a cryptographically random session token.</summary>
    /// <returns>A Base64-encoded 32-byte token.</returns>
    public static string Generate()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    ///     Confirms the caller-supplied token matches the token issued at host start.
    /// </summary>
    /// <param name="expected">The token generated when the host service started.</param>
    /// <param name="provided">The token from the RPC call, or <c>null</c> when omitted.</param>
    /// <exception cref="LocalRpcException">The token is missing or does not match.</exception>
    public static void Validate(string expected, string? provided)
    {
        if (string.IsNullOrEmpty(provided))
            ThrowInvalid();

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided!);
        if (expectedBytes.Length != providedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            ThrowInvalid();
        }
    }

    private static void ThrowInvalid() =>
        throw new LocalRpcException("Invalid or missing session token.")
        {
            ErrorCode = (int)JsonRpcErrorCode.InvalidParams,
        };
}

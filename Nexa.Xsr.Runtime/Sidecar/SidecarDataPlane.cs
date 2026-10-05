using Nexa.Sidecar.Protocol;

namespace Nexa.Xsr.Runtime;

/// <summary>Compatibility forwarding entry points. Portable codecs belong to Protocol.</summary>
public static class SidecarDataPlane
{
    public static byte[] EncodeRequest(uint contractId, string? argument) => SidecarDataMessages.EncodeRequest(contractId, argument);
    public static (uint ContractId, string Argument) DecodeRequest(ReadOnlySpan<byte> payload) => SidecarDataMessages.DecodeRequest(payload);
    public static byte[] EncodeResult(bool success, string value, string? errorCode) => SidecarDataMessages.EncodeResult(success, value, errorCode);
    public static (bool Success, string Value, string ErrorCode) DecodeResult(ReadOnlySpan<byte> payload) => SidecarDataMessages.DecodeResult(payload);
    public static byte[] EncodeStateDelta(uint contractId, ReadOnlySpan<byte> encodedValue) => SidecarDataMessages.EncodeStateDelta(contractId, encodedValue);
    public static (uint ContractId, byte[] EncodedValue) DecodeStateDelta(ReadOnlySpan<byte> payload) => SidecarDataMessages.DecodeStateDelta(payload);
    public static byte[] EncodeEvent(uint contractId, string payload) => SidecarDataMessages.EncodeEvent(contractId, payload);
    public static (uint ContractId, string Payload) DecodeEvent(ReadOnlySpan<byte> payload) => SidecarDataMessages.DecodeEvent(payload);
}

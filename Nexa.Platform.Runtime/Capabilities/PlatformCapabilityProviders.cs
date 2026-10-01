namespace Nexa.Services.Capabilities;

public static class PlatformCapabilityProviders
{
    public static IReadOnlyList<IMachineCapabilityProvider> CreateProviders() => Array.AsReadOnly<IMachineCapabilityProvider>([new RuntimeCapabilityProvider(), new MemoryCapabilityProvider()]);
}

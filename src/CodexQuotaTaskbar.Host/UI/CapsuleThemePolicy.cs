namespace CodexQuotaTaskbar.Host.UI;

internal enum CapsuleSurfaceMode
{
    Liquid,
    OpaqueSystem,
}

internal static class CapsuleThemePolicy
{
    internal static CapsuleSurfaceMode Resolve(bool highContrast, bool glassEnabled) =>
        highContrast || !glassEnabled ? CapsuleSurfaceMode.OpaqueSystem : CapsuleSurfaceMode.Liquid;
}

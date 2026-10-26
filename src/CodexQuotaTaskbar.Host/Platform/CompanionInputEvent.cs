namespace CodexQuotaTaskbar.Host.Platform;

internal enum CompanionInputKind { Key, MouseButton, Reset }
internal enum CompanionMouseButton { None, Left, Right }

// Ephemeral physical state for local rendering. This is not a character, key sequence or text.
// A physical key is the scan-code MakeCode with bit 8 set for an E0-prefixed key.
internal readonly record struct CompanionInputEvent(CompanionInputKind Kind, ushort PhysicalKey = 0,
    bool IsDown = false, CompanionMouseButton MouseButton = CompanionMouseButton.None);

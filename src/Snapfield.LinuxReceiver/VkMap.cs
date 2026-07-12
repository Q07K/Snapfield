namespace Snapfield.LinuxReceiver;

/// <summary>
/// Windows virtual-key code → Linux evdev KEY_* code, by PHYSICAL key position
/// (the hook forwards positions; the receiving session's XKB layout decides what
/// they type, so 한/영 lands on KEY_HANGEUL and ibus takes it from there).
/// </summary>
public static class VkMap
{
    private static readonly Dictionary<int, int> Map = new()
    {
        [0x08] = 14,   // Backspace
        [0x09] = 15,   // Tab
        [0x0D] = 28,   // Enter
        [0x13] = 119,  // Pause
        [0x14] = 58,   // CapsLock
        [0x15] = 122,  // 한/영 → KEY_HANGEUL
        [0x19] = 123,  // 한자 → KEY_HANJA
        [0x1B] = 1,    // Esc
        [0x20] = 57,   // Space
        [0x21] = 104,  // PageUp
        [0x22] = 109,  // PageDown
        [0x23] = 107,  // End
        [0x24] = 102,  // Home
        [0x25] = 105,  // Left
        [0x26] = 103,  // Up
        [0x27] = 106,  // Right
        [0x28] = 108,  // Down
        [0x2C] = 99,   // PrtScn → KEY_SYSRQ
        [0x2D] = 110,  // Insert
        [0x2E] = 111,  // Delete

        // Digit row: KEY_1..KEY_9 = 2..10, KEY_0 = 11.
        [0x30] = 11, [0x31] = 2, [0x32] = 3, [0x33] = 4, [0x34] = 5,
        [0x35] = 6, [0x36] = 7, [0x37] = 8, [0x38] = 9, [0x39] = 10,

        // Letters (evdev codes follow the physical QWERTY rows, not the alphabet).
        [0x41] = 30,  // A
        [0x42] = 48,  // B
        [0x43] = 46,  // C
        [0x44] = 32,  // D
        [0x45] = 18,  // E
        [0x46] = 33,  // F
        [0x47] = 34,  // G
        [0x48] = 35,  // H
        [0x49] = 23,  // I
        [0x4A] = 36,  // J
        [0x4B] = 37,  // K
        [0x4C] = 38,  // L
        [0x4D] = 50,  // M
        [0x4E] = 49,  // N
        [0x4F] = 24,  // O
        [0x50] = 25,  // P
        [0x51] = 16,  // Q
        [0x52] = 19,  // R
        [0x53] = 31,  // S
        [0x54] = 20,  // T
        [0x55] = 22,  // U
        [0x56] = 47,  // V
        [0x57] = 17,  // W
        [0x58] = 45,  // X
        [0x59] = 21,  // Y
        [0x5A] = 44,  // Z

        [0x5B] = 125, // LWin → KEY_LEFTMETA
        [0x5C] = 126, // RWin → KEY_RIGHTMETA
        [0x5D] = 127, // Menu → KEY_COMPOSE

        // Numpad.
        [0x60] = 82, [0x61] = 79, [0x62] = 80, [0x63] = 81, [0x64] = 75,
        [0x65] = 76, [0x66] = 77, [0x67] = 71, [0x68] = 72, [0x69] = 73,
        [0x6A] = 55,  // * → KEY_KPASTERISK
        [0x6B] = 78,  // + → KEY_KPPLUS
        [0x6D] = 74,  // - → KEY_KPMINUS
        [0x6E] = 83,  // . → KEY_KPDOT
        [0x6F] = 98,  // / → KEY_KPSLASH

        // F1..F12 (F11/F12 are non-contiguous in evdev).
        [0x70] = 59, [0x71] = 60, [0x72] = 61, [0x73] = 62, [0x74] = 63, [0x75] = 64,
        [0x76] = 65, [0x77] = 66, [0x78] = 67, [0x79] = 68, [0x7A] = 87, [0x7B] = 88,

        [0x90] = 69,  // NumLock
        [0x91] = 70,  // ScrollLock

        // Modifiers — the hook sends L/R variants; the generic VKs are fallbacks.
        [0x10] = 42, [0xA0] = 42,  // Shift → KEY_LEFTSHIFT
        [0xA1] = 54,               // RShift
        [0x11] = 29, [0xA2] = 29,  // Ctrl → KEY_LEFTCTRL
        [0xA3] = 97,               // RCtrl
        [0x12] = 56, [0xA4] = 56,  // Alt → KEY_LEFTALT
        [0xA5] = 100,              // RAlt (AltGr)

        // Volume / media.
        [0xAD] = 113, // Mute
        [0xAE] = 114, // VolumeDown
        [0xAF] = 115, // VolumeUp
        [0xB0] = 163, // NextTrack
        [0xB1] = 165, // PrevTrack
        [0xB2] = 166, // Stop
        [0xB3] = 164, // PlayPause

        // OEM punctuation (US physical positions).
        [0xBA] = 39,  // ;
        [0xBB] = 13,  // =
        [0xBC] = 51,  // ,
        [0xBD] = 12,  // -
        [0xBE] = 52,  // .
        [0xBF] = 53,  // /
        [0xC0] = 41,  // `
        [0xDB] = 26,  // [
        [0xDC] = 43,  // \
        [0xDD] = 27,  // ]
        [0xDE] = 40,  // '
    };

    public static bool TryGet(int vk, out int key) => Map.TryGetValue(vk, out key);

    /// <summary>Every evdev code the virtual keyboard must register at setup.</summary>
    public static IEnumerable<int> AllKeys() => Map.Values.Distinct();
}

package com.snapfield.receiver.input

import android.inputmethodservice.InputMethodService
import android.os.Handler
import android.os.Looper
import android.view.KeyEvent
import android.view.View

/**
 * A screenless keyboard (IME). The desktop forwards raw keyboard-hook events —
 * Windows virtual-key code + up/down — and this service turns them into text on
 * whatever field currently has focus: printable keys are committed as characters
 * (honouring Shift), navigation/edit keys and Ctrl/Alt shortcuts go through
 * sendKeyEvent. Injecting into arbitrary apps is only possible from an IME, so
 * the user selects "Snapfield 키보드" while being controlled; it shows no UI, so
 * all typing comes from the PC.
 */
class SnapfieldImeService : InputMethodService() {

    companion object {
        @Volatile var instance: SnapfieldImeService? = null
        val isActive get() = instance != null

        // Windows virtual-key codes we care about.
        private const val VK_BACK = 0x08
        private const val VK_TAB = 0x09
        private const val VK_RETURN = 0x0D
        private const val VK_SPACE = 0x20
    }

    private val main = Handler(Looper.getMainLooper())

    // Modifier state, tracked from their own down/up events.
    @Volatile private var shift = false
    @Volatile private var ctrl = false
    @Volatile private var alt = false

    // 한/영: the PC forwards VK_HANGUL (0x15) from the 한/영 key; we compose
    // dubeolsik Hangul locally since the PC's IME state never leaves the PC.
    private val hangul = HangulComposer()
    @Volatile private var hangulMode = false

    // A screenless IME: nothing to show, all input arrives over the network.
    override fun onCreateInputView(): View? = null
    override fun onEvaluateInputViewShown(): Boolean = false
    override fun onEvaluateFullscreenMode(): Boolean = false

    override fun onCreate() {
        super.onCreate()
        instance = this
    }

    override fun onStartInput(attribute: android.view.inputmethod.EditorInfo?, restarting: Boolean) {
        super.onStartInput(attribute, restarting)
        hangul.flush() // a new field starts clean — drop any half-composed syllable
        // As the selected IME we're allowed to read the clipboard — good moment
        // to ship any copy that happened while reading was blocked.
        com.snapfield.receiver.ReceiverService.instance?.trySyncClipboard()
    }

    override fun onDestroy() {
        instance = null
        super.onDestroy()
    }

    /** Feed one forwarded key transition (called from the network thread). */
    fun onKey(vk: Int, down: Boolean) {
        // Modifiers: update state, emit nothing.
        when (vk) {
            0x10, 0xA0, 0xA1 -> { shift = down; return }
            0x11, 0xA2, 0xA3 -> { ctrl = down; return }
            0x12, 0xA4, 0xA5 -> { alt = down; return }
        }
        main.post { handle(vk, down) }
    }

    private fun handle(vk: Int, down: Boolean) {
        val ic = currentInputConnection ?: return

        // 한/영 toggle (the PC's own IME state stays on the PC — we track ours).
        if (vk == 0x15) {
            if (down) { flushComposition(ic); hangulMode = !hangulMode }
            return
        }

        // Jamo-level backspace while a syllable is composing.
        if (vk == VK_BACK && hangulMode && hangul.isComposing) {
            if (!down) return
            hangul.backspace()
            if (hangul.isComposing) ic.setComposingText(hangul.composing, 1)
            else ic.commitText("", 1) // clears the (now empty) composing region
            return
        }

        val keyCode = specialKeyCode(vk)
        if (keyCode != 0) {
            // Navigation / editing keys end the composition, then act.
            if (down) flushComposition(ic)
            ic.sendKeyEvent(KeyEvent(if (down) KeyEvent.ACTION_DOWN else KeyEvent.ACTION_UP, keyCode))
            return
        }

        if (!down) return // printable characters commit on key-down only

        // Ctrl/Alt shortcuts (Ctrl+A, Ctrl+C …): send as a key event with meta so
        // the app's own handling fires, instead of typing a stray letter.
        if ((ctrl || alt) && vk in 0x41..0x5A) {
            flushComposition(ic)
            val code = KeyEvent.KEYCODE_A + (vk - 0x41)
            var meta = 0
            if (ctrl) meta = meta or KeyEvent.META_CTRL_ON or KeyEvent.META_CTRL_LEFT_ON
            if (alt) meta = meta or KeyEvent.META_ALT_ON or KeyEvent.META_ALT_LEFT_ON
            if (shift) meta = meta or KeyEvent.META_SHIFT_ON or KeyEvent.META_SHIFT_LEFT_ON
            val now = android.os.SystemClock.uptimeMillis()
            ic.sendKeyEvent(KeyEvent(now, now, KeyEvent.ACTION_DOWN, code, 0, meta))
            ic.sendKeyEvent(KeyEvent(now, now, KeyEvent.ACTION_UP, code, 0, meta))
            return
        }

        // Hangul mode: letters compose; everything else flushes then types.
        if (hangulMode && vk in 0x41..0x5A) {
            val jamo = hangul.jamoFor('a' + (vk - 0x41), shift)
            if (jamo != null) {
                val done = hangul.feed(jamo)
                if (done.isNotEmpty()) ic.commitText(done, 1) // replaces the composing region
                if (hangul.isComposing) ic.setComposingText(hangul.composing, 1)
                return
            }
        }

        val ch = vkToChar(vk, shift) ?: return
        flushComposition(ic)
        ic.commitText(ch.toString(), 1)
    }

    /** Commits whatever syllable is mid-composition (no-op when idle). */
    private fun flushComposition(ic: android.view.inputmethod.InputConnection) {
        if (!hangul.isComposing) return
        val done = hangul.flush()
        if (done.isNotEmpty()) ic.commitText(done, 1)
        else ic.finishComposingText()
    }

    /** VK → Android keycode for keys that must be real events, else 0.
    /// (Esc never reaches here — the service maps it to the global Back.) */
    private fun specialKeyCode(vk: Int): Int = when (vk) {
        VK_BACK -> KeyEvent.KEYCODE_DEL
        VK_RETURN -> KeyEvent.KEYCODE_ENTER
        VK_TAB -> KeyEvent.KEYCODE_TAB
        0x25 -> KeyEvent.KEYCODE_DPAD_LEFT
        0x26 -> KeyEvent.KEYCODE_DPAD_UP
        0x27 -> KeyEvent.KEYCODE_DPAD_RIGHT
        0x28 -> KeyEvent.KEYCODE_DPAD_DOWN
        0x2E -> KeyEvent.KEYCODE_FORWARD_DEL
        0x24 -> KeyEvent.KEYCODE_MOVE_HOME
        0x23 -> KeyEvent.KEYCODE_MOVE_END
        0x21 -> KeyEvent.KEYCODE_PAGE_UP
        0x22 -> KeyEvent.KEYCODE_PAGE_DOWN
        else -> 0
    }

    /** VK → character for a US layout, honouring Shift. Null = not printable here. */
    private fun vkToChar(vk: Int, shift: Boolean): Char? {
        when (vk) {
            VK_SPACE -> return ' '
            in 0x41..0x5A -> { // A–Z
                val c = 'a' + (vk - 0x41)
                return if (shift) c.uppercaseChar() else c
            }
            in 0x30..0x39 -> { // 0–9 row
                return if (!shift) '0' + (vk - 0x30)
                else ")!@#$%^&*("[vk - 0x30]
            }
            in 0x60..0x69 -> return '0' + (vk - 0x60) // numpad digits
            0x6A -> return '*'
            0x6B -> return '+'
            0x6D -> return '-'
            0x6E -> return '.'
            0x6F -> return '/'
        }
        // OEM punctuation (US layout): unshifted / shifted.
        val pair: Pair<Char, Char>? = when (vk) {
            0xBA -> ';' to ':'
            0xBB -> '=' to '+'
            0xBC -> ',' to '<'
            0xBD -> '-' to '_'
            0xBE -> '.' to '>'
            0xBF -> '/' to '?'
            0xC0 -> '`' to '~'
            0xDB -> '[' to '{'
            0xDC -> '\\' to '|'
            0xDD -> ']' to '}'
            0xDE -> '\'' to '"'
            else -> null
        }
        return pair?.let { if (shift) it.second else it.first }
    }
}

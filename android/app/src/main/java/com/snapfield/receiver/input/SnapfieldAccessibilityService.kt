package com.snapfield.receiver.input

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.graphics.Path
import android.graphics.PixelFormat
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import kotlin.math.abs
import kotlin.math.hypot

/**
 * The injection half of the receiver: draws the PC cursor as an overlay (an
 * accessibility service may add overlay windows without extra permissions) and
 * replays clicks/drags/scrolls as touch gestures via dispatchGesture — the
 * DeskDock approach; no root required, but the user must enable the service.
 */
class SnapfieldAccessibilityService : AccessibilityService() {

    companion object {
        @Volatile var instance: SnapfieldAccessibilityService? = null
        val isEnabled get() = instance != null
    }

    private val main = Handler(Looper.getMainLooper())
    private var windowManager: WindowManager? = null
    private var cursor: CursorOverlay? = null
    private var cursorParams: WindowManager.LayoutParams? = null

    // Latest-wins cursor position: moves arrive at mouse-polling rate, so the
    // network thread stores the newest point and posts at most one UI update.
    @Volatile private var pendingX = 0
    @Volatile private var pendingY = 0
    private val movePosted = java.util.concurrent.atomic.AtomicBoolean(false)

    // Button state for click-vs-drag detection.
    private var downX = 0; private var downY = 0
    private var lastX = 0; private var lastY = 0
    private var downAtMs = 0L

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        windowManager = getSystemService(WINDOW_SERVICE) as WindowManager
    }

    override fun onDestroy() {
        instance = null
        main.post { removeCursor() }
        super.onDestroy()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}
    override fun onInterrupt() {}

    // ── cursor overlay ────────────────────────────────────────────────────────
    fun showCursor() = main.post {
        if (cursor != null) return@post
        val wm = windowManager ?: return@post
        val params = WindowManager.LayoutParams(
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                or WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
                or WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT,
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            width = 32; height = 36
        }
        val view = CursorOverlay(this)
        try {
            wm.addView(view, params)
            cursor = view
            cursorParams = params
        } catch (_: Exception) { /* overlay refused — gestures still work */ }
    }

    fun hideCursor() = main.post { removeCursor() }

    private fun removeCursor() {
        val view = cursor ?: return
        try { windowManager?.removeView(view) } catch (_: Exception) {}
        cursor = null
        cursorParams = null
    }

    fun moveCursor(x: Int, y: Int) {
        pendingX = x
        pendingY = y
        lastX = x
        lastY = y
        if (movePosted.compareAndSet(false, true)) main.post {
            movePosted.set(false)
            val params = cursorParams ?: return@post
            params.x = pendingX
            params.y = pendingY
            cursor?.let { try { windowManager?.updateViewLayout(it, params) } catch (_: Exception) {} }
        }
    }

    // ── gestures ──────────────────────────────────────────────────────────────
    fun onButton(down: Boolean) {
        if (down) {
            downX = lastX; downY = lastY
            downAtMs = System.currentTimeMillis()
            return
        }
        // On release decide: little movement = tap, real movement = drag swipe.
        val upX = lastX; val upY = lastY
        val dist = hypot((upX - downX).toDouble(), (upY - downY).toDouble())
        main.post {
            val path = Path()
            if (dist < 24) {
                path.moveTo(downX.toFloat(), downY.toFloat())
                dispatch(path, 60L)
            } else {
                path.moveTo(downX.toFloat(), downY.toFloat())
                path.lineTo(upX.toFloat(), upY.toFloat())
                val held = (System.currentTimeMillis() - downAtMs).coerceIn(120L, 800L)
                dispatch(path, held)
            }
        }
    }

    fun onWheel(delta: Int, horizontal: Boolean) {
        val x = lastX.toFloat(); val y = lastY.toFloat()
        val amount = 300f * (abs(delta) / 120f).coerceIn(0.5f, 3f)
        // Wheel up (delta > 0) shows earlier content = finger swipes DOWN.
        val sign = if (delta > 0) 1f else -1f
        main.post {
            val path = Path()
            path.moveTo(x, y)
            if (horizontal) path.lineTo(x + sign * amount, y)
            else path.lineTo(x, y + sign * amount)
            dispatch(path, 150L)
        }
    }

    private fun dispatch(path: Path, durationMs: Long) {
        try {
            val stroke = GestureDescription.StrokeDescription(path, 0, durationMs)
            dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
        } catch (_: Exception) { /* gesture refused (screen off / secure surface) */ }
    }

    // ── global actions (mapped from PC keys) ──────────────────────────────────
    /** PrtScn on the PC → a real screenshot on the phone (Android 9+). */
    fun takeScreenshot() = main.post {
        if (android.os.Build.VERSION.SDK_INT >= 28)
            try { performGlobalAction(GLOBAL_ACTION_TAKE_SCREENSHOT) } catch (_: Exception) {}
    }

    /** Win key on the PC → the phone's home. */
    fun goHome() = main.post {
        try { performGlobalAction(GLOBAL_ACTION_HOME) } catch (_: Exception) {}
    }
}

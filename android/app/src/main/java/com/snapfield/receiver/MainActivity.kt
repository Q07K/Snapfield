package com.snapfield.receiver

import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.util.TypedValue
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView
import com.snapfield.receiver.input.SnapfieldAccessibilityService
import java.net.NetworkInterface

/**
 * Receiver control panel, Aurora-toned: IP + pairing code up top (what the PC
 * asks for), then the two switches that make it work — start waiting, and the
 * accessibility permission that allows cursor/tap injection.
 */
class MainActivity : Activity() {

    private lateinit var status: TextView
    private lateinit var startButton: Button
    private lateinit var accessibilityButton: Button
    private lateinit var keyboardButton: Button

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 1)

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(0xFF0A0A11.toInt())
            setPadding(dp(24), dp(48), dp(24), dp(24))
        }

        root.addView(text("Snapfield", 24f, 0xFFEAECF6, bold = true))
        root.addView(text("안드로이드 수신 기기", 13f, 0xFF9A9EAD).withTopMargin(dp(2)))

        // What the PC-side sheet asks for: IP + the green 6-digit code.
        root.addView(card().also { card ->
            card.addView(text("이 기기의 IP (조작 기기에서 입력)", 11f, 0xFF9A9EAD))
            card.addView(text(localIps(), 18f, 0xFFEBB257, bold = true, mono = true).withTopMargin(dp(2)))
            card.addView(text("연결 코드", 11f, 0xFF9A9EAD).withTopMargin(dp(12)))
            card.addView(text(Prefs.pin(this).chunked(1).joinToString(" "), 30f, 0xFF5FCF8A, bold = true, mono = true))
        }.withTopMargin(dp(20)))

        startButton = Button(this).apply { setOnClickListener { toggleReceiver() } }
        root.addView(startButton.withTopMargin(dp(16)))

        accessibilityButton = Button(this).apply {
            setOnClickListener { startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) }
        }
        root.addView(accessibilityButton.withTopMargin(dp(8)))

        // Keyboard: tapping either enables the IME (settings) or switches to it
        // (picker), depending on where the user is in the two-step setup.
        keyboardButton = Button(this).apply {
            setOnClickListener {
                if (isImeEnabled()) {
                    (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).showInputMethodPicker()
                } else {
                    startActivity(Intent(Settings.ACTION_INPUT_METHOD_SETTINGS))
                }
            }
        }
        root.addView(keyboardButton.withTopMargin(dp(8)))

        status = text("", 12f, 0xFF7E8296)
        root.addView(status.withTopMargin(dp(14)))

        root.addView(text(
            "PC의 배치 탭에서 이 기기를 모니터처럼 원하는 자리에 놓으세요.\n" +
                "커서가 화면 경계를 넘어 들어오면 탭·드래그·스크롤이 동작합니다.\n\n" +
                "키보드를 쓰려면: ①'Snapfield 키보드'를 켜고 ②입력할 때 " +
                "'Snapfield 키보드'로 전환하세요. (화면 키보드는 사라지고 PC에서만 입력됩니다)",
            11f, 0xFF5B6070,
        ).withTopMargin(dp(18)))

        setContentView(root)
        ReceiverService.listener = { s -> runOnUiThread { refresh(s) } }
    }

    override fun onResume() {
        super.onResume()
        refresh(ReceiverService.statusText)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        // Focused = allowed to read the clipboard; sync anything copied earlier.
        if (hasFocus) ReceiverService.instance?.trySyncClipboard()
    }

    override fun onDestroy() {
        ReceiverService.listener = null
        super.onDestroy()
    }

    private fun toggleReceiver() {
        if (ReceiverService.running) ReceiverService.stop(this) else ReceiverService.start(this)
        startButton.postDelayed({ refresh(ReceiverService.statusText) }, 300L)
    }

    private fun refresh(statusText: String) {
        startButton.text = if (ReceiverService.running) "수신 중지" else "수신 시작 (연결 대기)"
        accessibilityButton.text =
            if (SnapfieldAccessibilityService.isEnabled) "접근성 권한 켜짐 ✓"
            else "접근성 권한 켜기 (커서·탭에 필요)"
        keyboardButton.text = when {
            isImeSelected() -> "Snapfield 키보드 사용 중 ✓"
            isImeEnabled() -> "입력할 땐 'Snapfield 키보드'로 전환"
            else -> "Snapfield 키보드 켜기 (키보드 입력에 필요)"
        }
        status.text = statusText
    }

    private fun isImeEnabled(): Boolean {
        val imm = getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager
        return imm.enabledInputMethodList.any { it.packageName == packageName }
    }

    private fun isImeSelected(): Boolean {
        val current = Settings.Secure.getString(contentResolver, Settings.Secure.DEFAULT_INPUT_METHOD)
        return current?.startsWith(packageName) == true
    }

    // ── tiny view helpers (no layout XML, no dependencies) ───────────────────
    private fun text(value: String, sizeSp: Float, color: Long, bold: Boolean = false, mono: Boolean = false) =
        TextView(this).apply {
            text = value
            setTextColor(color.toInt())
            setTextSize(TypedValue.COMPLEX_UNIT_SP, sizeSp)
            if (bold) typeface = if (mono) Typeface.create(Typeface.MONOSPACE, Typeface.BOLD) else Typeface.DEFAULT_BOLD
            else if (mono) typeface = Typeface.MONOSPACE
            setLineSpacing(0f, 1.3f)
        }

    private fun card() = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(16), dp(14), dp(16), dp(14))
        background = android.graphics.drawable.GradientDrawable().apply {
            setColor(0xFF161620.toInt())
            cornerRadius = dp(12).toFloat()
            setStroke(dp(1), 0xFF262838.toInt())
        }
    }

    private fun <T : android.view.View> T.withTopMargin(px: Int): T {
        layoutParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = px }
        return this
    }

    private fun dp(v: Int): Int =
        TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v.toFloat(), resources.displayMetrics).toInt()

    private fun localIps(): String = try {
        NetworkInterface.getNetworkInterfaces().asSequence()
            .filter { it.isUp && !it.isLoopback }
            .flatMap { it.inetAddresses.asSequence() }
            .filterIsInstance<java.net.Inet4Address>()
            .map { it.hostAddress ?: "" }
            .filter { it.isNotEmpty() && !it.startsWith("169.254.") }
            .distinct()
            .joinToString("   ")
            .ifEmpty { "IP를 찾지 못했습니다 (Wi-Fi 확인)" }
    } catch (_: Exception) { "IP를 찾지 못했습니다" }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        // Leave the receiver running in the background, like the desktop tray.
        moveTaskToBack(true)
    }
}

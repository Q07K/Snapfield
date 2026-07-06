package com.snapfield.receiver

import android.animation.ObjectAnimator
import android.animation.ValueAnimator
import android.app.Activity
import android.app.AlertDialog
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.view.inputmethod.InputMethodManager
import android.widget.Button
import android.widget.LinearLayout
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import com.snapfield.receiver.input.SnapfieldAccessibilityService
import java.net.NetworkInterface

/**
 * Receiver control panel. Top-down: a status hero (waiting pulse / connected /
 * being-driven glow), a setup checklist that walks the four gates in order and
 * disappears once done, the pairing-code card with copy/hide/regenerate, the
 * keep-awake switches, and start/stop.
 */
class MainActivity : Activity() {

    private lateinit var hero: LinearLayout
    private lateinit var heroDot: View
    private lateinit var heroTitle: TextView
    private lateinit var heroSub: TextView
    private var heroPulse: ObjectAnimator? = null

    private lateinit var checklist: LinearLayout
    private val stepViews = mutableListOf<Triple<TextView, TextView, LinearLayout>>() // (number, label, row)

    private lateinit var codeText: TextView
    private var codeHidden = false

    private lateinit var startButton: Button
    private lateinit var status: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(arrayOf(android.Manifest.permission.POST_NOTIFICATIONS), 1)

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(0xFF0A0A11.toInt())
            setPadding(dp(22), dp(40), dp(22), dp(22))
        }

        root.addView(text("Snapfield", 22f, 0xFFEAECF6, bold = true))

        // ── hero: what state am I in, at a glance ────────────────────────────
        heroDot = View(this)
        heroTitle = text("", 15f, 0xFFEAECF6, bold = true).apply { gravity = Gravity.CENTER }
        heroSub = text("", 10.5f, 0xFF9A9EAD).apply { gravity = Gravity.CENTER }
        hero = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(dp(14), dp(16), dp(14), dp(16))
            addView(heroDot, LinearLayout.LayoutParams(dp(12), dp(12)).apply {
                gravity = Gravity.CENTER_HORIZONTAL; bottomMargin = dp(8)
            })
            addView(heroTitle)
            addView(heroSub)
        }
        root.addView(hero.withTopMargin(dp(14)))

        // ── setup checklist: the four gates, in order, until they're done ────
        checklist = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        addStep("수신 시작") { toggleReceiver(startOnly = true) }
        addStep("접근성 권한 켜기 (커서·탭)") { startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) }
        addStep("Snapfield 키보드 켜기") { startActivity(Intent(Settings.ACTION_INPUT_METHOD_SETTINGS)) }
        addStep("입력할 때 키보드 전환") {
            (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).showInputMethodPicker()
        }
        root.addView(checklist.withTopMargin(dp(12)))

        // ── pairing code card ─────────────────────────────────────────────────
        val codeCard = card()
        codeCard.addView(text("연결 코드 (PC에서 입력)", 10f, 0xFF9A9EAD))
        codeText = text("", 26f, 0xFF5FCF8A, bold = true, mono = true)
        codeCard.addView(codeText)
        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(smallButton("복사") { copyPin() })
        actions.addView(smallButton("가리기") { codeHidden = !codeHidden; refresh(ReceiverService.statusText) }.withStartMargin(dp(6)))
        actions.addView(smallButton("재발급…") { confirmRegeneratePin() }.withStartMargin(dp(6)))
        codeCard.addView(actions.withTopMargin(dp(8)))
        codeCard.addView(text("이 기기의 IP", 10f, 0xFF9A9EAD).withTopMargin(dp(12)))
        codeCard.addView(text(localIps(), 13f, 0xFFEBB257, bold = true, mono = true))
        root.addView(codeCard.withTopMargin(dp(12)))

        // ── keep-awake: a sleeping screen can't take gestures ────────────────
        val awakeCard = card()
        awakeCard.addView(switchRow(
            "조작 중 화면 유지", "PC 커서가 있는 동안 화면이 꺼지지 않음",
            Prefs.keepAwakeControlled(this),
        ) { on -> Prefs.setKeepAwakeControlled(this, on); ReceiverService.instance?.refreshKeepAwake() })
        awakeCard.addView(divider().withTopMargin(dp(9)))
        awakeCard.addView(switchRow(
            "수신 중 항상 화면 유지", "대기 중에도 유지 — 배터리 소모 큼",
            Prefs.keepAwakeAlways(this),
        ) { on -> Prefs.setKeepAwakeAlways(this, on); ReceiverService.instance?.refreshKeepAwake() }.withTopMargin(dp(9)))
        root.addView(awakeCard.withTopMargin(dp(12)))

        startButton = Button(this).apply { setOnClickListener { toggleReceiver() } }
        root.addView(startButton.withTopMargin(dp(12)))

        status = text("", 10.5f, 0xFF5B6070)
        root.addView(status.withTopMargin(dp(10)))

        setContentView(android.widget.ScrollView(this).apply { addView(root) })
        ReceiverService.listener = { s -> runOnUiThread { refresh(s) } }
    }

    override fun onResume() {
        super.onResume()
        refresh(ReceiverService.statusText)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            ReceiverService.instance?.trySyncClipboard()
            refresh(ReceiverService.statusText)
        }
    }

    override fun onDestroy() {
        ReceiverService.listener = null
        super.onDestroy()
    }

    // ── state → pixels ─────────────────────────────────────────────────────────
    private fun refresh(statusText: String) {
        // Hero.
        val kind = ReceiverService.stateKind
        val name = ReceiverService.controllerName ?: "조작 기기"
        val (bg, stroke) = when (kind) {
            "live" -> 0xFF10162B to 0xFF5B87EE
            "connected" -> 0xFF12201A to 0xFF274434
            "waiting" -> 0xFF12201A to 0xFF274434
            else -> 0xFF13131C to 0xFF262838
        }
        hero.background = GradientDrawable().apply {
            setColor(bg.toInt()); cornerRadius = dp(14).toFloat(); setStroke(dp(kind.let { if (it == "live") 2 else 1 }), stroke.toInt())
        }
        heroDot.background = GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(when (kind) { "live" -> 0xFF5B87EE; "off" -> 0xFF3A3D50; else -> 0xFF5FCF8A }.toInt())
        }
        heroTitle.text = when (kind) {
            "live" -> "$name이(가) 조작 중"
            "connected" -> "$name에 연결됨"
            "waiting" -> "연결 대기 중…"
            else -> "수신 꺼짐"
        }
        heroSub.text = when (kind) {
            "live" -> "커서가 이 화면에 있습니다"
            "connected" -> "PC에서 커서를 밀어 넘어오세요"
            "waiting" -> "PC의 '기기 추가' 목록에 이 기기가 표시됩니다"
            else -> "아래에서 수신을 시작하세요"
        }
        heroPulse?.cancel(); heroPulse = null
        heroDot.alpha = 1f
        if (kind == "waiting") {
            heroPulse = ObjectAnimator.ofFloat(heroDot, "alpha", 0.25f, 1f).apply {
                duration = 900; repeatMode = ValueAnimator.REVERSE; repeatCount = ValueAnimator.INFINITE; start()
            }
        }

        // Checklist: highlight the first undone step; hide once 1–3 are done
        // (step 4 is situational — switch when you actually type).
        val done = listOf(
            ReceiverService.running,
            SnapfieldAccessibilityService.isEnabled,
            isImeEnabled(),
            isImeSelected(),
        )
        val setupDone = done[0] && done[1] && done[2]
        checklist.visibility = if (setupDone) View.GONE else View.VISIBLE
        if (!setupDone) {
            val current = done.indexOfFirst { !it }
            stepViews.forEachIndexed { i, (num, label, row) ->
                val isDone = done[i]
                num.text = if (isDone) "✓" else "${i + 1}"
                num.background = GradientDrawable().apply {
                    shape = GradientDrawable.OVAL
                    setColor(when { isDone -> 0xFF1D3527; i == current -> 0xFF5B87EE; else -> 0xFF1B1C26 }.toInt())
                }
                num.setTextColor((if (isDone) 0xFF5FCF8A else if (i == current) 0xFFFFFFFF else 0xFF5B6070).toInt())
                label.setTextColor((if (isDone) 0xFF6E7286 else 0xFFEAECF6).toInt())
                row.alpha = if (isDone) 0.6f else 1f
                row.background = GradientDrawable().apply {
                    setColor(0xFF161620.toInt()); cornerRadius = dp(10).toFloat()
                    setStroke(dp(1), (if (i == current) 0xFF2A3A66 else 0xFF262838).toInt())
                }
            }
        }

        // Code, start/stop, status line.
        val pin = Prefs.pin(this)
        codeText.text = if (codeHidden) "• • • • • •" else pin.chunked(1).joinToString(" ")
        startButton.text = if (ReceiverService.running) "수신 중지" else "수신 시작 (연결 대기)"
        status.text = statusText
    }

    // ── actions ───────────────────────────────────────────────────────────────
    private fun toggleReceiver(startOnly: Boolean = false) {
        if (ReceiverService.running) { if (!startOnly) ReceiverService.stop(this) }
        else ReceiverService.start(this)
        startButton.postDelayed({ refresh(ReceiverService.statusText) }, 300L)
    }

    private fun copyPin() {
        val cm = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
        cm.setPrimaryClip(ClipData.newPlainText("Snapfield", Prefs.pin(this)))
        Toast.makeText(this, "연결 코드 복사됨", Toast.LENGTH_SHORT).show()
    }

    private fun confirmRegeneratePin() {
        AlertDialog.Builder(this)
            .setTitle("연결 코드 재발급")
            .setMessage("새 코드는 다음 연결부터 적용됩니다.\nPC에 저장된 이전 코드로는 더 이상 연결할 수 없어요.")
            .setPositiveButton("재발급") { _, _ ->
                Prefs.regeneratePin(this)
                refresh(ReceiverService.statusText)
                Toast.makeText(this, "새 연결 코드가 발급되었습니다", Toast.LENGTH_SHORT).show()
            }
            .setNegativeButton("취소", null)
            .show()
    }

    // ── view builders (no XML, no dependencies) ───────────────────────────────
    private fun addStep(labelText: String, onTap: () -> Unit) {
        val num = TextView(this).apply {
            gravity = Gravity.CENTER
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 10f)
            typeface = Typeface.DEFAULT_BOLD
        }
        val label = text(labelText, 12f, 0xFFEAECF6)
        val go = text("열기 ›", 11f, 0xFF5B87EE, bold = true)
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(11), dp(9), dp(11), dp(9))
            addView(num, LinearLayout.LayoutParams(dp(20), dp(20)))
            addView(label, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f).apply { marginStart = dp(9) })
            addView(go)
            setOnClickListener { onTap() }
        }
        checklist.addView(row.withTopMargin(if (stepViews.isEmpty()) 0 else dp(6)))
        stepViews.add(Triple(num, label, row))
    }

    private fun switchRow(title: String, subtitle: String, checked: Boolean, onChange: (Boolean) -> Unit): LinearLayout {
        val sw = Switch(this).apply {
            isChecked = checked
            setOnCheckedChangeListener { _, on -> onChange(on) }
        }
        return LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            addView(LinearLayout(this@MainActivity).apply {
                orientation = LinearLayout.VERTICAL
                addView(text(title, 12f, 0xFFEAECF6, bold = true))
                addView(text(subtitle, 9.5f, 0xFF5B6070))
            }, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
            addView(sw)
        }
    }

    private fun smallButton(label: String, onTap: () -> Unit) = Button(this).apply {
        text = label
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f)
        minHeight = 0; minimumHeight = 0
        setPadding(dp(12), dp(6), dp(12), dp(6))
        setOnClickListener { onTap() }
    }

    private fun divider() = View(this).apply {
        setBackgroundColor(0xFF1E2030.toInt())
        layoutParams = LinearLayout.LayoutParams(LinearLayout.LayoutParams.MATCH_PARENT, dp(1))
    }

    private fun card() = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(14), dp(12), dp(14), dp(12))
        background = GradientDrawable().apply {
            setColor(0xFF161620.toInt()); cornerRadius = dp(12).toFloat(); setStroke(dp(1), 0xFF262838.toInt())
        }
    }

    private fun text(value: String, sizeSp: Float, color: Long, bold: Boolean = false, mono: Boolean = false) =
        TextView(this).apply {
            text = value
            setTextColor(color.toInt())
            setTextSize(TypedValue.COMPLEX_UNIT_SP, sizeSp)
            if (bold) typeface = if (mono) Typeface.create(Typeface.MONOSPACE, Typeface.BOLD) else Typeface.DEFAULT_BOLD
            else if (mono) typeface = Typeface.MONOSPACE
            setLineSpacing(0f, 1.25f)
        }

    private fun <T : View> T.withTopMargin(px: Int): T {
        layoutParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = px }
        return this
    }

    private fun <T : View> T.withStartMargin(px: Int): T {
        layoutParams = LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT, LinearLayout.LayoutParams.WRAP_CONTENT
        ).apply { marginStart = px }
        return this
    }

    private fun dp(v: Int): Int =
        TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v.toFloat(), resources.displayMetrics).toInt()

    private fun isImeEnabled(): Boolean {
        val imm = getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager
        return imm.enabledInputMethodList.any { it.packageName == packageName }
    }

    private fun isImeSelected(): Boolean {
        val current = Settings.Secure.getString(contentResolver, Settings.Secure.DEFAULT_INPUT_METHOD)
        return current?.startsWith(packageName) == true
    }

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

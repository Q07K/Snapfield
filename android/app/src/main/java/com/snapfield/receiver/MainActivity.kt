package com.snapfield.receiver

import android.app.Activity
import android.os.Bundle
import android.util.TypedValue
import android.widget.LinearLayout
import android.widget.TextView

/**
 * Placeholder shell for the Android receiver. Proves the packaging/CI pipeline
 * end-to-end (repo → GitHub Actions → installable APK) before the real work:
 * the protocol port (ECDH → AES-GCM frames, discovery beacon) and input
 * injection (overlay cursor + AccessibilityService, Shizuku as the power path).
 */
class MainActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val title = TextView(this).apply {
            text = "Snapfield"
            setTextColor(0xFFEAECF6.toInt())
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 26f)
            typeface = android.graphics.Typeface.DEFAULT_BOLD
        }
        val body = TextView(this).apply {
            text = "안드로이드 수신 기기 — 개발 중\n\n" +
                "이 기기를 PC들과 같은 물리 평면에 올려, PC 커서가 " +
                "화면 경계를 넘어 들어오게 만드는 것이 목표입니다.\n\n" +
                "다음 단계:\n" +
                "  1. 프로토콜 이식 (암호화 TCP + 발견 비컨)\n" +
                "  2. 오버레이 커서 + 접근성 탭/스크롤\n" +
                "  3. 배치 캔버스 연동"
            setTextColor(0xFF9A9EAD.toInt())
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
            setLineSpacing(0f, 1.35f)
            setPadding(0, dp(20), 0, 0)
        }

        setContentView(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(0xFF0A0A11.toInt())
            setPadding(dp(28), dp(64), dp(28), dp(28))
            addView(title)
            addView(body)
        })
    }

    private fun dp(v: Int): Int =
        TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v.toFloat(), resources.displayMetrics).toInt()
}

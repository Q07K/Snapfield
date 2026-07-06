package com.snapfield.receiver

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.Point
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.provider.Settings
import android.view.WindowManager
import com.snapfield.receiver.input.SnapfieldAccessibilityService
import com.snapfield.receiver.net.Beacon
import com.snapfield.receiver.net.MonitorState
import com.snapfield.receiver.net.MsgType
import com.snapfield.receiver.net.NetMessage
import com.snapfield.receiver.net.PeerLink

/**
 * The receiver session as a foreground service: listens for one controller,
 * answers Hello with this device's screen (so it lands on the PC's physical
 * plane like any monitor), and routes input messages to the accessibility
 * service. Mirrors the desktop receiver's lifecycle: re-listen on drop, keep
 * listening on a wrong-pin attempt.
 */
class ReceiverService : Service() {

    companion object {
        const val PORT = 45654
        @Volatile var running = false
        @Volatile var statusText = "대기 전"
        @Volatile var listener: ((String) -> Unit)? = null
        @Volatile var instance: ReceiverService? = null

        /** Hero state for the UI: off / waiting / connected / live (being driven). */
        @Volatile var stateKind = "off"
        @Volatile var controllerName: String? = null

        fun start(context: Context) {
            context.startForegroundService(Intent(context, ReceiverService::class.java))
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, ReceiverService::class.java))
        }
    }

    private val main = Handler(Looper.getMainLooper())
    private var link: PeerLink? = null
    private var beacon: Beacon? = null
    @Volatile private var disposed = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        instance = this
        startForeground(1, buildNotification())
        running = true
        stateKind = "waiting"
        beacon = Beacon(deviceName(), PORT).also { it.start() }
        listen()
        // Fires on every copy; the read itself only succeeds while we're allowed
        // (foreground, or the Snapfield keyboard is the selected IME).
        (getSystemService(CLIPBOARD_SERVICE) as ClipboardManager)
            .addPrimaryClipChangedListener(clipChanged)
        applyKeepAwake()
        setStatus("연결 대기 중 — 포트 $PORT")
    }

    override fun onDestroy() {
        instance = null
        disposed = true
        running = false
        try {
            (getSystemService(CLIPBOARD_SERVICE) as ClipboardManager)
                .removePrimaryClipChangedListener(clipChanged)
        } catch (_: Exception) {}
        beacon?.stop()
        link?.close()
        stateKind = "off"
        controllerName = null
        SnapfieldAccessibilityService.instance?.hideCursor()
        SnapfieldAccessibilityService.instance?.setKeepAwake(false)
        setStatus("중지됨")
        super.onDestroy()
    }

    /** Keep-awake policy: always-mode holds while running; controlled-mode only
    /// while the PC cursor is on this device. */
    private fun applyKeepAwake() {
        val on = Prefs.keepAwakeAlways(this) ||
            (Prefs.keepAwakeControlled(this) && stateKind == "live")
        SnapfieldAccessibilityService.instance?.setKeepAwake(on)
    }

    /** Called from the UI when a keep-awake switch flips. */
    fun refreshKeepAwake() = applyKeepAwake()

    // ── session ───────────────────────────────────────────────────────────────
    private fun listen() {
        if (disposed) return
        link?.close()
        link = PeerLink(
            port = PORT,
            pinProvider = { Prefs.pin(this) },
            onConnected = { setStatus("암호화 연결됨 — 컨트롤러의 hello 대기 중 …") },
            onMessage = ::onMessage,
            onDisconnected = { reason ->
                SnapfieldAccessibilityService.instance?.hideCursor()
                if (disposed) return@PeerLink
                stateKind = "waiting"
                controllerName = null
                applyKeepAwake()
                if (reason.startsWith("AUTH:")) setStatus("연결 코드가 일치하지 않습니다. 계속 대기합니다.")
                else setStatus("연결 끊김: $reason — 다시 대기합니다.")
                main.postDelayed({ listen() }, 500L)
            },
        ).also { it.listen() }
    }

    private fun onMessage(msg: NetMessage) {
        when (msg.type) {
            MsgType.Hello -> {
                // Controller identified itself (pin already verified by the
                // handshake) — answer with this device's screen.
                link?.send(NetMessage.hello(deviceName(), listOf(screenAsMonitor())))
                stateKind = "connected"
                controllerName = msg.machineId
                setStatus("'${msg.machineId ?: "controller"}'에 연결됨. 제어 대기 중.")
            }
            MsgType.CursorMove -> SnapfieldAccessibilityService.instance?.moveCursor(msg.x, msg.y)
            MsgType.MouseButton -> when (msg.button) {
                0 -> SnapfieldAccessibilityService.instance?.onButton(msg.down)
                1 -> if (msg.down) SnapfieldAccessibilityService.instance?.goBack()    // 우클릭 = 뒤로
                2 -> if (msg.down) SnapfieldAccessibilityService.instance?.goRecents() // 휠클릭 = 최근 앱
            }
            MsgType.MouseWheel -> SnapfieldAccessibilityService.instance?.onWheel(msg.wheelDelta, msg.horizontal)
            MsgType.ControlEnter -> {
                stateKind = "live"
                SnapfieldAccessibilityService.instance?.showCursor()
                applyKeepAwake() // the screen must not sleep under the PC cursor
                setStatus("조작 기기가 이 화면을 넘겨받았습니다.")
            }
            MsgType.ControlLeave -> {
                stateKind = "connected"
                SnapfieldAccessibilityService.instance?.hideCursor()
                applyKeepAwake()
                setStatus("조작 기기가 이 화면을 떠났습니다.")
            }
            MsgType.Clipboard -> msg.text?.let { applyClipboard(it) }
            MsgType.ClipboardImage -> msg.text?.let { applyClipboardImage(it) }
            MsgType.Key -> routeKey(msg.vk, msg.down)
            else -> { /* Layout / files: not consumed on Android yet */ }
        }
    }

    // ── this device as a monitor on the plane ─────────────────────────────────
    private fun screenAsMonitor(): MonitorState {
        val wm = getSystemService(WINDOW_SERVICE) as WindowManager
        val size = Point()
        @Suppress("DEPRECATION")
        wm.defaultDisplay.getRealSize(size)
        val dm = resources.displayMetrics
        // xdpi/ydpi are the panel's physical density; devices occasionally lie,
        // and the desktop's true-size correction can fix that afterwards.
        val widthMm = size.x / dm.xdpi.toDouble() * 25.4
        val heightMm = size.y / dm.ydpi.toDouble() * 25.4
        return MonitorState(
            machineId = deviceName(),
            deviceId = "android-display0",
            displayName = Build.MODEL,
            pixelLeft = 0,
            pixelTop = 0,
            pixelWidth = size.x,
            pixelHeight = size.y,
            physicalXMm = 0.0,
            physicalYMm = 0.0,
            physicalWidthMm = widthMm,
            physicalHeightMm = heightMm,
            dpiScale = dm.density.toDouble(),
            isInternal = true,
            // sw600dp is the standard phone/tablet boundary (DeviceKind: 3=phone, 4=tablet)
            kind = if (resources.configuration.smallestScreenWidthDp >= 600) 4 else 3,
        )
    }

    private fun deviceName(): String =
        Settings.Global.getString(contentResolver, "device_name")?.takeIf { it.isNotBlank() }
            ?: Build.MODEL

    // ── clipboard, both directions ────────────────────────────────────────────
    // PC → phone: write is allowed from the service. Phone → PC: Android 10+
    // only lets the FOCUSED app or the DEFAULT IME read the clipboard — so the
    // change listener fires always, but the read only succeeds while the
    // Snapfield keyboard is selected or our activity is up. We also retry at
    // those moments (IME session start / activity resume) to catch copies that
    // happened while reading was blocked.
    @Volatile private var lastApplied = ""    // last text we wrote (came from the PC)
    @Volatile private var lastSent = ""       // last text we shipped to the PC
    @Volatile private var lastImageMd5 = ""   // last image applied OR sent (both directions)

    private val clipChanged = ClipboardManager.OnPrimaryClipChangedListener { trySyncClipboard() }

    /** Reads the clipboard if Android lets us right now, and ships new content
    /// (image URI or text) to the PC. */
    fun trySyncClipboard() = main.post {
        try {
            val cm = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
            val item = cm.primaryClip?.getItemAt(0) ?: return@post
            val uri = item.uri
            if (uri != null) { syncImage(uri); return@post }
            val text = item.coerceToText(this)?.toString() ?: return@post
            if (text.isEmpty() || text.length > 500_000) return@post
            if (text == lastApplied || text == lastSent) return@post // echo guard, same as desktop
            lastSent = text
            link?.send(NetMessage(type = MsgType.Clipboard, text = text))
            setStatus("클립보드 전송 (${text.length}자)")
        } catch (_: Exception) { /* read blocked right now — the next window will catch it */ }
    }

    /** Clipboard image (a content URI) → PNG → the PC clipboard. */
    private fun syncImage(uri: android.net.Uri) {
        // Our own provider URI can only mean "the PC just sent this" — bouncing
        // it back re-encoded used to defeat the md5 guard (PNG encoders aren't
        // byte-stable) and clobber the PC clipboard with the echo.
        if (uri.authority == ClipImageProvider.AUTHORITY) return
        try {
            // Hash the RAW bytes first: stable for the same source, no re-encode.
            val raw = contentResolver.openInputStream(uri)?.use { it.readBytes() } ?: return
            if (raw.size > 24 * 1024 * 1024) { setStatus("이미지가 너무 큽니다 — 전송 생략."); return }
            val rawMd5 = md5(raw)
            if (rawMd5 == lastImageMd5) return
            lastImageMd5 = rawMd5

            val bmp = android.graphics.BitmapFactory.decodeByteArray(raw, 0, raw.size) ?: return
            val out = java.io.ByteArrayOutputStream()
            bmp.compress(android.graphics.Bitmap.CompressFormat.PNG, 100, out)
            val png = out.toByteArray()
            if (png.size > 8 * 1024 * 1024) { setStatus("이미지가 너무 큽니다(>8MB) — 전송 생략."); return }
            link?.send(NetMessage(type = MsgType.ClipboardImage,
                text = android.util.Base64.encodeToString(png, android.util.Base64.NO_WRAP)))
            setStatus("클립보드 이미지 전송 (${png.size / 1024}KB)")
        } catch (_: Exception) { /* unreadable image — skip */ }
    }

    private fun applyClipboard(text: String) = main.post {
        try {
            val cm = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
            lastApplied = text
            cm.setPrimaryClip(ClipData.newPlainText("Snapfield", text))
            setStatus("클립보드 수신 (${text.length}자)")
        } catch (_: Exception) { /* background clipboard write refused on some ROMs */ }
    }

    /** PC clipboard image → PNG file → our provider URI on the phone clipboard. */
    private fun applyClipboardImage(b64: String) = main.post {
        try {
            val png = android.util.Base64.decode(b64, android.util.Base64.DEFAULT)
            lastImageMd5 = md5(png) // the change listener must not bounce it back
            ClipImageProvider.file(this).writeBytes(png)
            val cm = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newUri(contentResolver, "Snapfield", ClipImageProvider.URI))
            setStatus("클립보드 이미지 수신 (${png.size / 1024}KB) — 붙여넣기 가능")
        } catch (_: Exception) { /* decode/write failed — skip */ }
    }

    // ── PC keys → Android-flavoured actions ───────────────────────────────────
    // Device-wide keys resolve here (the IME only reaches focused text fields):
    //   Esc = 뒤로, Win = 홈, Alt+Tab = 최근 앱, PrtScn = 캡처→PC 클립보드,
    //   볼륨/음소거/미디어 키 = 폰 볼륨/미디어. Everything else types via the IME.
    @Volatile private var altDown = false

    private fun routeKey(vk: Int, down: Boolean) {
        when (vk) {
            0x12, 0xA4, 0xA5 -> altDown = down // track, then fall through to the IME
        }
        when {
            vk == 0x2C && down -> { capturePrtScn(); return }                       // PrtScn
            vk == 0x1B && down -> { SnapfieldAccessibilityService.instance?.goBack(); return }   // Esc
            (vk == 0x5B || vk == 0x5C) && down -> { SnapfieldAccessibilityService.instance?.goHome(); return } // Win
            vk == 0x09 && down && altDown -> { SnapfieldAccessibilityService.instance?.goRecents(); return }   // Alt+Tab
            vk == 0xAF && down -> { adjustVolume(android.media.AudioManager.ADJUST_RAISE); return }
            vk == 0xAE && down -> { adjustVolume(android.media.AudioManager.ADJUST_LOWER); return }
            vk == 0xAD && down -> { adjustVolume(android.media.AudioManager.ADJUST_TOGGLE_MUTE); return }
            vk == 0xB3 && down -> { mediaKey(android.view.KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE); return }
            vk == 0xB0 && down -> { mediaKey(android.view.KeyEvent.KEYCODE_MEDIA_NEXT); return }
            vk == 0xB1 && down -> { mediaKey(android.view.KeyEvent.KEYCODE_MEDIA_PREVIOUS); return }
            vk in intArrayOf(0x2C, 0x1B, 0x5B, 0x5C, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB3) -> return // their key-ups
        }
        com.snapfield.receiver.input.SnapfieldImeService.instance?.onKey(vk, down)
    }

    private fun adjustVolume(direction: Int) = main.post {
        try {
            (getSystemService(AUDIO_SERVICE) as android.media.AudioManager)
                .adjustStreamVolume(android.media.AudioManager.STREAM_MUSIC, direction,
                    android.media.AudioManager.FLAG_SHOW_UI)
        } catch (_: Exception) {}
    }

    private fun mediaKey(keyCode: Int) = main.post {
        try {
            val am = getSystemService(AUDIO_SERVICE) as android.media.AudioManager
            am.dispatchMediaKeyEvent(android.view.KeyEvent(android.view.KeyEvent.ACTION_DOWN, keyCode))
            am.dispatchMediaKeyEvent(android.view.KeyEvent(android.view.KeyEvent.ACTION_UP, keyCode))
        } catch (_: Exception) {}
    }

    /** PrtScn: capture this screen and put it on the PC clipboard (Windows semantics). */
    private fun capturePrtScn() {
        SnapfieldAccessibilityService.instance?.captureScreenPng { png ->
            if (png == null) {
                // Most common cause: the canTakeScreenshot capability is read when
                // the service is enabled — after an app update it needs a re-toggle.
                setStatus("화면 캡처 실패 — 접근성 권한을 껐다 다시 켜보세요.")
                return@captureScreenPng
            }
            if (png.size > 8 * 1024 * 1024) { setStatus("캡처가 너무 큽니다(>8MB) — 전송 생략."); return@captureScreenPng }
            lastImageMd5 = md5(png)
            link?.send(NetMessage(type = MsgType.ClipboardImage,
                text = android.util.Base64.encodeToString(png, android.util.Base64.NO_WRAP)))
            setStatus("화면 캡처를 PC 클립보드로 전송 (${png.size / 1024}KB)")
        } ?: setStatus("캡처하려면 접근성 권한이 필요합니다.")
    }

    private fun md5(bytes: ByteArray): String =
        java.security.MessageDigest.getInstance("MD5").digest(bytes)
            .joinToString("") { "%02x".format(it) }

    // ── plumbing ──────────────────────────────────────────────────────────────
    private fun setStatus(s: String) {
        statusText = s
        main.post { listener?.invoke(s) }
    }

    private fun buildNotification(): Notification {
        val channelId = "receiver"
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(channelId, "수신 대기", NotificationManager.IMPORTANCE_LOW)
        )
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE
        )
        return Notification.Builder(this, channelId)
            .setContentTitle("Snapfield 수신 대기 중")
            .setContentText("PC 커서가 이 기기로 넘어올 수 있습니다")
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }
}

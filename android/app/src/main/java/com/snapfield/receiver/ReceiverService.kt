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
        startForeground(1, buildNotification())
        running = true
        beacon = Beacon(deviceName(), PORT).also { it.start() }
        listen()
        setStatus("연결 대기 중 — 포트 $PORT")
    }

    override fun onDestroy() {
        disposed = true
        running = false
        beacon?.stop()
        link?.close()
        SnapfieldAccessibilityService.instance?.hideCursor()
        setStatus("중지됨")
        super.onDestroy()
    }

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
                setStatus("'${msg.machineId ?: "controller"}'에 연결됨. 제어 대기 중.")
            }
            MsgType.CursorMove -> SnapfieldAccessibilityService.instance?.moveCursor(msg.x, msg.y)
            MsgType.MouseButton -> if (msg.button == 0) SnapfieldAccessibilityService.instance?.onButton(msg.down)
            MsgType.MouseWheel -> SnapfieldAccessibilityService.instance?.onWheel(msg.wheelDelta, msg.horizontal)
            MsgType.ControlEnter -> {
                SnapfieldAccessibilityService.instance?.showCursor()
                setStatus("조작 기기가 이 화면을 넘겨받았습니다.")
            }
            MsgType.ControlLeave -> {
                SnapfieldAccessibilityService.instance?.hideCursor()
                setStatus("조작 기기가 이 화면을 떠났습니다.")
            }
            MsgType.Clipboard -> msg.text?.let { applyClipboard(it) }
            MsgType.Key -> { /* keyboard needs the IME route — next milestone */ }
            else -> { /* Layout / images / files: not consumed on Android yet */ }
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
        )
    }

    private fun deviceName(): String =
        Settings.Global.getString(contentResolver, "device_name")?.takeIf { it.isNotBlank() }
            ?: Build.MODEL

    // ── clipboard (PC → phone; the reverse is restricted on Android 10+) ─────
    private fun applyClipboard(text: String) = main.post {
        try {
            val cm = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newPlainText("Snapfield", text))
            setStatus("클립보드 수신 (${text.length}자)")
        } catch (_: Exception) { /* background clipboard write refused on some ROMs */ }
    }

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

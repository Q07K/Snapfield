package com.snapfield.receiver.net

import org.json.JSONArray
import org.json.JSONObject

/**
 * Wire message — a 1:1 port of the desktop `NetMessage` record. The C# side
 * serialises with System.Text.Json defaults: PascalCase property names, enums
 * as integers, and default-valued fields omitted; this codec mirrors that
 * exactly so the two implementations are wire-compatible.
 */
object MsgType {
    const val Hello = 0
    const val CursorMove = 1
    const val MouseButton = 2
    const val MouseWheel = 3
    const val ControlEnter = 4
    const val ControlLeave = 5
    const val Key = 6
    const val Clipboard = 7
    const val AuthFail = 8
    const val ClipboardImage = 9
    const val Layout = 10
    const val ClipboardFiles = 11
}

/** One monitor as shipped in Hello/Layout — mirrors the desktop `MonitorState`. */
data class MonitorState(
    val machineId: String,
    val deviceId: String,
    val displayName: String,
    val pixelLeft: Int,
    val pixelTop: Int,
    val pixelWidth: Int,
    val pixelHeight: Int,
    val physicalXMm: Double,
    val physicalYMm: Double,
    val physicalWidthMm: Double,
    val physicalHeightMm: Double,
    val dpiScale: Double,
    val isInternal: Boolean,
    /** DeviceKind on the desktop: 0 unspecified, 1 monitor, 2 laptop, 3 phone, 4 tablet. */
    val kind: Int = 0,
) {
    fun toJson(): JSONObject = JSONObject().apply {
        put("MachineId", machineId)
        put("DeviceId", deviceId)
        put("DisplayName", displayName)
        if (pixelLeft != 0) put("PixelLeft", pixelLeft)
        if (pixelTop != 0) put("PixelTop", pixelTop)
        put("PixelWidth", pixelWidth)
        put("PixelHeight", pixelHeight)
        if (physicalXMm != 0.0) put("PhysicalXMm", physicalXMm)
        if (physicalYMm != 0.0) put("PhysicalYMm", physicalYMm)
        put("PhysicalWidthMm", physicalWidthMm)
        put("PhysicalHeightMm", physicalHeightMm)
        put("DpiScale", dpiScale)
        if (isInternal) put("IsInternal", true)
        if (kind != 0) put("Kind", kind)
    }
}

data class NetMessage(
    val type: Int = MsgType.Hello,
    val machineId: String? = null,
    val monitors: List<MonitorState>? = null,
    val x: Int = 0,
    val y: Int = 0,
    val button: Int = 0,
    val down: Boolean = false,
    val wheelDelta: Int = 0,
    val horizontal: Boolean = false,
    val vk: Int = 0,
    val scan: Int = 0,
    val extended: Boolean = false,
    val text: String? = null,
) {
    /** UTF-8 JSON body, defaults omitted — byte-compatible with the desktop. */
    fun toJsonBytes(): ByteArray {
        val o = JSONObject()
        if (type != 0) o.put("Type", type)
        machineId?.let { o.put("MachineId", it) }
        monitors?.let { list -> o.put("Monitors", JSONArray().apply { list.forEach { put(it.toJson()) } }) }
        if (x != 0) o.put("X", x)
        if (y != 0) o.put("Y", y)
        if (button != 0) o.put("Button", button)
        if (down) o.put("Down", true)
        if (wheelDelta != 0) o.put("WheelDelta", wheelDelta)
        if (horizontal) o.put("Horizontal", true)
        if (vk != 0) o.put("Vk", vk)
        if (scan != 0) o.put("Scan", scan)
        if (extended) o.put("Extended", true)
        text?.let { o.put("Text", it) }
        return o.toString().toByteArray(Charsets.UTF_8)
    }

    companion object {
        fun hello(machineId: String, monitors: List<MonitorState>) =
            NetMessage(type = MsgType.Hello, machineId = machineId, monitors = monitors)

        fun fromBody(body: ByteArray, length: Int): NetMessage? = try {
            val o = JSONObject(String(body, 0, length, Charsets.UTF_8))
            NetMessage(
                type = o.optInt("Type", 0),
                machineId = o.optString("MachineId").takeIf { o.has("MachineId") },
                monitors = null, // Layout/Hello monitor lists aren't consumed on Android yet
                x = o.optInt("X", 0),
                y = o.optInt("Y", 0),
                button = o.optInt("Button", 0),
                down = o.optBoolean("Down", false),
                wheelDelta = o.optInt("WheelDelta", 0),
                horizontal = o.optBoolean("Horizontal", false),
                vk = o.optInt("Vk", 0),
                scan = o.optInt("Scan", 0),
                extended = o.optBoolean("Extended", false),
                text = o.optString("Text").takeIf { o.has("Text") },
            )
        } catch (_: Exception) { null }
    }
}

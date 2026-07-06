package com.snapfield.receiver

import android.content.Context
import kotlin.random.Random

/** Tiny settings store: pairing code + keep-awake switches. */
object Prefs {
    private const val FILE = "snapfield"

    private fun sp(context: Context) = context.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    fun pin(context: Context): String {
        sp(context).getString("pin", null)?.let { return it }
        return regeneratePin(context)
    }

    /** Issues a fresh pairing code; applies from the next handshake on. */
    fun regeneratePin(context: Context): String {
        val pin = Random.nextInt(100000, 1000000).toString()
        sp(context).edit().putString("pin", pin).apply()
        return pin
    }

    /** Keep the screen on while the PC cursor is on this device (default on —
    /// a sleeping screen can't take gestures, which reads as "it broke"). */
    fun keepAwakeControlled(context: Context) = sp(context).getBoolean("keepAwakeControlled", true)
    fun setKeepAwakeControlled(context: Context, on: Boolean) =
        sp(context).edit().putBoolean("keepAwakeControlled", on).apply()

    /** Keep the screen on the whole time the receiver runs (battery-hungry). */
    fun keepAwakeAlways(context: Context) = sp(context).getBoolean("keepAwakeAlways", false)
    fun setKeepAwakeAlways(context: Context, on: Boolean) =
        sp(context).edit().putBoolean("keepAwakeAlways", on).apply()
}

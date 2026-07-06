package com.snapfield.receiver

import android.content.Context
import kotlin.random.Random

/** Tiny settings store — just the pairing code for now (generated once). */
object Prefs {
    private const val FILE = "snapfield"

    fun pin(context: Context): String {
        val sp = context.getSharedPreferences(FILE, Context.MODE_PRIVATE)
        sp.getString("pin", null)?.let { return it }
        val pin = Random.nextInt(100000, 1000000).toString()
        sp.edit().putString("pin", pin).apply()
        return pin
    }
}

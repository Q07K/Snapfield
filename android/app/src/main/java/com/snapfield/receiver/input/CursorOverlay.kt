package com.snapfield.receiver.input

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Path
import android.view.View

/** The PC cursor, drawn as a small arrow overlay on top of everything. */
class CursorOverlay(context: Context) : View(context) {

    private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        style = Paint.Style.FILL
    }
    private val outline = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.BLACK
        style = Paint.Style.STROKE
        strokeWidth = 3f
    }
    private val arrow = Path()

    override fun onDraw(canvas: Canvas) {
        // Classic pointer silhouette, ~28px tall, hotspot at (0,0).
        arrow.reset()
        arrow.moveTo(2f, 2f)
        arrow.lineTo(2f, 26f)
        arrow.lineTo(9f, 20f)
        arrow.lineTo(14f, 30f)
        arrow.lineTo(18f, 28f)
        arrow.lineTo(13f, 18f)
        arrow.lineTo(21f, 17f)
        arrow.close()
        canvas.drawPath(arrow, outline)
        canvas.drawPath(arrow, fill)
    }
}

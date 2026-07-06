package com.snapfield.receiver

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import java.io.File

/**
 * Serves the last clipboard image received from the PC. Android clipboards
 * carry image URIs, not bytes, so pasting apps read the PNG through this
 * provider; the system clipboard handles the permission grant
 * (grantUriPermissions in the manifest, provider stays unexported).
 */
class ClipImageProvider : ContentProvider() {

    companion object {
        const val AUTHORITY = "com.snapfield.receiver.clip"
        val URI: Uri = Uri.parse("content://$AUTHORITY/clip.png")

        fun file(context: android.content.Context): File =
            File(File(context.cacheDir, "clip").apply { mkdirs() }, "clip.png")
    }

    override fun onCreate() = true

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor =
        ParcelFileDescriptor.open(file(context!!), ParcelFileDescriptor.MODE_READ_ONLY)

    override fun getType(uri: Uri) = "image/png"

    // Paste targets commonly query name/size before reading.
    override fun query(uri: Uri, projection: Array<out String>?, selection: String?,
                       selectionArgs: Array<out String>?, sortOrder: String?): Cursor {
        val cols = projection ?: arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE)
        val cursor = MatrixCursor(cols)
        val f = file(context!!)
        cursor.addRow(cols.map { c ->
            when (c) {
                OpenableColumns.DISPLAY_NAME -> "snapfield-clipboard.png"
                OpenableColumns.SIZE -> f.length()
                else -> null
            }
        })
        return cursor
    }

    override fun insert(uri: Uri, values: ContentValues?) = null
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?) = 0
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?) = 0
}

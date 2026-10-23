package com.clipfx.share

import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.os.ParcelFileDescriptor
import android.os.StatFs
import android.provider.DocumentsContract
import android.provider.OpenableColumns
import androidx.documentfile.provider.DocumentFile
import com.clipfx.share.core.*
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.IOException
import java.nio.ByteBuffer

object Folders {
    val names = listOf("Videos", "Images", "Music", "Documents", "Apps", "Archives", "Folders", "Other")

    /** "ClipFx Share" at the top of internal storage; Download/ClipFx Share without all-files access. */
    fun defaultRoot(ctx: Context): File {
        val top = File(Environment.getExternalStorageDirectory(), "ClipFx Share")
        val fallback = File(Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS), "ClipFx Share")
        return if (Permissions.hasAllFiles(ctx)) top else fallback
    }

    /** Creates the folder and its type subfolders so they're there from day one. */
    fun prepare(root: File) {
        runCatching { root.mkdirs(); names.forEach { File(root, it).mkdirs() } }
    }
}

// ------------------------------------------------------------------ reading what to send

/** Opens content:// URIs and file paths for random-access reads. */
class AndroidOpener(private val ctx: Context) : SourceOpener {
    override fun open(ref: String): SourceReader {
        if (!ref.startsWith("content://")) return FileSource(File(ref))
        val pfd = ctx.contentResolver.openFileDescriptor(Uri.parse(ref), "r") ?: throw IOException("Can't open file.")
        val stream = FileInputStream(pfd.fileDescriptor)
        val ch = stream.channel
        return object : SourceReader {
            override val length: Long get() = runCatching { ch.size() }.getOrDefault(pfd.statSize)
            override fun read(buf: ByteArray, off: Int, len: Int, position: Long): Int = ch.read(ByteBuffer.wrap(buf, off, len), position)
            override fun close() { ch.close(); stream.close(); pfd.close() }
        }
    }
}

// ------------------------------------------------------------------ saving to a folder the user picked (SAF)

class SafTarget(private val ctx: Context, private val treeUri: Uri) : SaveTarget {
    private val root = DocumentFile.fromTreeUri(ctx, treeUri) ?: throw IOException("Folder not available.")
    private val lock = Any()
    override val display: String = root.name ?: "Selected folder"

    private fun dir(segments: List<String>, create: Boolean): DocumentFile? = synchronized(lock) {
        var d = root
        for (s in segments) {
            d = d.findFile(s)?.takeIf { it.isDirectory } ?: if (create) d.createDirectory(s) ?: return null else return null
        }
        d
    }

    private fun split(localPath: String): Pair<List<String>, String> {
        val seg = PathSafety.segments(localPath)
        return seg.dropLast(1) to seg.last()
    }

    override fun openPart(localPath: String, size: Long): PartWriter {
        val (dirs, name) = split(localPath)
        val parent = dir(dirs, true) ?: throw IOException("Can't create folder.")
        val partName = name + PART_SUFFIX
        val doc = synchronized(lock) { parent.findFile(partName) ?: parent.createFile("application/octet-stream", partName) } ?: throw IOException("Can't create file.")
        val pfd = ctx.contentResolver.openFileDescriptor(doc.uri, "rw") ?: throw IOException("Can't write file.")
        val out = FileOutputStream(pfd.fileDescriptor)
        val ch = out.channel
        if (runCatching { ch.size() }.getOrDefault(0) != size) runCatching { ch.truncate(size) }
        return object : PartWriter {
            override fun write(buf: ByteArray, off: Int, len: Int, position: Long) {
                val bb = ByteBuffer.wrap(buf, off, len); var p = position
                while (bb.hasRemaining()) p += ch.write(bb, p)
            }
            override fun close() { runCatching { ch.force(false) }; ch.close(); out.close(); pfd.close() }
        }
    }

    override fun exists(localPath: String): Boolean { val (d, n) = split(localPath); return dir(d, false)?.findFile(n) != null }

    override fun finish(localPath: String, conflict: String, mtime: Long, size: Long): String {
        val (dirs, name) = split(localPath)
        val parent = dir(dirs, true) ?: throw IOException("Can't create folder.")
        synchronized(lock) {
            var part = parent.findFile(name + PART_SUFFIX)
            if (part == null) {
                if (size == 0L) part = parent.createFile("application/octet-stream", name + PART_SUFFIX) else throw IOException("Missing data for $name.")
            }
            var finalName = name
            parent.findFile(name)?.let { existing ->
                if (conflict == "overwrite") existing.delete()
                else finalName = PathSafety.unique(name) { parent.findFile(it) != null }
            }
            val renamed = DocumentsContract.renameDocument(ctx.contentResolver, part!!.uri, finalName) ?: throw IOException("Can't rename $name.")
            return renamed.toString()
        }
    }

    override fun makeDir(localPath: String) { dir(PathSafety.segments(localPath), true) }
    override fun discard(localPath: String) { val (d, n) = split(localPath); dir(d, false)?.findFile(n + PART_SUFFIX)?.delete() }
}

/** Picks the storage by root: content:// is a picked folder, anything else a path. */
class AndroidTargets(private val ctx: Context) : SaveTargets {
    override fun forRoot(root: String): SaveTarget =
        if (root.startsWith("content://")) SafTarget(ctx, Uri.parse(root))
        else object : SaveTarget by FileTarget(root) {
            override fun freeBytes(): Long? = runCatching { val f = File(root).let { var x: File? = it; while (x != null && !x.exists()) x = x.parentFile; x }; StatFs(f!!.path).availableBytes }.getOrNull()
            override fun maxFileSize(): Long? = if (isFat(root)) 4L * 1024 * 1024 * 1024 - 1 else null
        }

    /** SD cards are often FAT32, which can't hold files over 4 GB. */
    private fun isFat(root: String): Boolean = runCatching {
        File("/proc/mounts").readLines().map { it.split(' ') }.filter { it.size > 2 && root.startsWith(it[1]) }
            .maxByOrNull { it[1].length }?.get(2)?.let { it == "vfat" || it == "msdos" } ?: false
    }.getOrDefault(false)
}

// ------------------------------------------------------------------ describing picked files

data class Picked(val name: String, val size: Long, val ref: String, val mtime: Long = 0, val isDir: Boolean = false)

object Pick {
    fun describe(ctx: Context, uri: Uri): Picked? = runCatching {
        ctx.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { c ->
            if (!c.moveToFirst()) return null
            val name = c.getString(0) ?: "file"
            var size = if (c.isNull(1)) -1 else c.getLong(1)
            if (size < 0) size = ctx.contentResolver.openFileDescriptor(uri, "r")?.use { it.statSize } ?: 0
            Picked(name, size, uri.toString())
        }
    }.getOrNull()

    /** Expands a picked folder tree into items, keeping its structure. */
    fun folder(ctx: Context, tree: Uri, startIndex: Int): List<SourceItem> {
        val root = DocumentFile.fromTreeUri(ctx, tree) ?: return emptyList()
        val out = ArrayList<SourceItem>()
        var n = startIndex
        val rootName = root.name ?: "Folder"
        out.add(SourceItem(ItemInfo(n++, rootName, 0, 0, ItemType.DIR), null))
        fun walk(d: DocumentFile, prefix: String) {
            for (c in d.listFiles()) {
                val p = "$prefix/${c.name}"
                if (c.isDirectory) { out.add(SourceItem(ItemInfo(n++, p, 0, 0, ItemType.DIR), null)); walk(c, p) }
                else out.add(SourceItem(ItemInfo(n++, p, c.length(), c.lastModified(), ItemType.FILE), c.uri.toString()))
            }
        }
        walk(root, rootName)
        return out
    }
}

package com.clipfx.share.core

import java.io.Closeable
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.channels.FileChannel
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.nio.file.attribute.FileTime

/** Random-access writes into a partial file. */
interface PartWriter : Closeable {
    fun write(buf: ByteArray, off: Int, len: Int, position: Long)
}

/** Random-access reads from something being sent. */
interface SourceReader : Closeable {
    val length: Long
    fun read(buf: ByteArray, off: Int, len: Int, position: Long): Int
}

/** Opens what the user picked to send (a file path, or on Android a content URI). */
fun interface SourceOpener { fun open(ref: String): SourceReader }

/** One save location. Paths are relative and already sorted/cleaned. */
interface SaveTarget {
    val display: String
    fun openPart(localPath: String, size: Long): PartWriter
    fun exists(localPath: String): Boolean
    /** Moves the finished part into place. Returns where it ended up. */
    fun finish(localPath: String, conflict: String, mtime: Long, size: Long): String
    fun makeDir(localPath: String)
    fun discard(localPath: String)
    fun freeBytes(): Long? = null
    /** Largest file the storage can hold (FAT32: 4 GB), or null if unlimited. */
    fun maxFileSize(): Long? = null
}

fun interface SaveTargets { fun forRoot(root: String): SaveTarget }

const val PART_SUFFIX = ".cfpart"

// ---------------------------------------------------------------- plain files

class FileSource(private val file: File) : SourceReader {
    private val ch: FileChannel = RandomAccessFile(file, "r").channel
    override val length: Long get() = ch.size()
    override fun read(buf: ByteArray, off: Int, len: Int, position: Long): Int = ch.read(ByteBuffer.wrap(buf, off, len), position)
    override fun close() = ch.close()
}

val FileOpener = SourceOpener { FileSource(File(it)) }

class FileTarget(rootPath: String) : SaveTarget {
    private val root = File(rootPath)
    override val display: String = root.absolutePath

    private fun resolve(localPath: String): File {
        val f = PathSafety.segments(localPath).fold(root) { acc, seg -> File(acc, seg) }
        require(f.canonicalPath.startsWith(root.canonicalPath)) { "Path escapes the save folder." }
        return f
    }

    override fun openPart(localPath: String, size: Long): PartWriter {
        val part = File(resolve(localPath).path + PART_SUFFIX)
        part.parentFile?.mkdirs()
        val raf = RandomAccessFile(part, "rw")
        if (raf.length() != size) raf.setLength(size)
        val ch = raf.channel
        return object : PartWriter {
            override fun write(buf: ByteArray, off: Int, len: Int, position: Long) {
                val bb = ByteBuffer.wrap(buf, off, len)
                var p = position
                while (bb.hasRemaining()) p += ch.write(bb, p)
            }
            override fun close() { ch.close(); raf.close() }
        }
    }

    override fun exists(localPath: String) = resolve(localPath).exists()

    override fun finish(localPath: String, conflict: String, mtime: Long, size: Long): String {
        var final = resolve(localPath)
        val part = File(final.path + PART_SUFFIX)
        final.parentFile?.mkdirs()
        if (!part.exists()) {
            if (size == 0L) part.createNewFile() else throw java.io.IOException("Missing data for ${final.name}.")
        }
        if (final.exists()) {
            if (conflict == "overwrite") final.delete()
            else final = File(final.parentFile, PathSafety.unique(final.name) { File(final.parentFile, it).exists() })
        }
        Files.move(part.toPath(), final.toPath(), StandardCopyOption.ATOMIC_MOVE)
        if (mtime > 0) runCatching { Files.setLastModifiedTime(final.toPath(), FileTime.fromMillis(mtime)) }
        return final.absolutePath
    }

    override fun makeDir(localPath: String) { resolve(localPath).mkdirs() }
    override fun discard(localPath: String) { File(resolve(localPath).path + PART_SUFFIX).delete() }
    override fun freeBytes(): Long? = root.let { var r: File? = it; while (r != null && !r.exists()) r = r.parentFile; r?.usableSpace }
}

val FileTargets = SaveTargets { FileTarget(it) }

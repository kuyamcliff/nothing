package com.clipfx.share.core

object PathSafety {
    private val reserved = setOf(
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    )
    private const val INVALID = "<>:\"|?*\\"

    fun cleanSegment(seg: String): String {
        val sb = StringBuilder(seg.length)
        for (c in seg) sb.append(if (c.code < 32 || INVALID.indexOf(c) >= 0) '_' else c)
        var s = sb.toString().trim().trimEnd('.')
        if (s.isEmpty() || s == "." || s == "..") s = "_"
        if (s.substringBefore('.').uppercase() in reserved) s = "_$s"
        if (s.toByteArray().size > 240) s = s.take(120)
        return s
    }

    /** Relative path to cleaned segments that can never escape the save folder. */
    fun segments(relative: String): List<String> =
        relative.replace('\\', '/').split('/').filter { it.isNotEmpty() && it != "." && it != ".." }
            .map(::cleanSegment).ifEmpty { listOf("_") }

    /** "name.ext" to "name (2).ext" until it doesn't exist. */
    fun unique(name: String, exists: (String) -> Boolean): String {
        if (!exists(name)) return name
        val dot = name.lastIndexOf('.').let { if (it <= 0) name.length else it }
        val stem = name.substring(0, dot); val ext = name.substring(dot)
        var i = 2
        while (true) { val n = "$stem ($i)$ext"; if (!exists(n)) return n; i++ }
    }
}

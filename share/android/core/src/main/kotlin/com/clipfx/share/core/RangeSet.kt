package com.clipfx.share.core

/** Sorted, merged set of half-open byte ranges [start, end). */
class RangeSet(initial: List<List<Long>>? = null) {
    private val r = ArrayList<LongArray>()

    init { initial?.forEach { if (it.size == 2) add(it[0], it[1]) } }

    @Synchronized
    fun add(start: Long, end: Long) {
        if (end <= start) return
        var s = start; var e = end
        var i = 0
        while (i < r.size && r[i][1] < s) i++
        var j = i
        while (j < r.size && r[j][0] <= e) { s = minOf(s, r[j][0]); e = maxOf(e, r[j][1]); j++ }
        for (k in j - 1 downTo i) r.removeAt(k)
        r.add(i, longArrayOf(s, e))
    }

    val covered: Long @Synchronized get() = r.sumOf { it[1] - it[0] }

    @Synchronized
    fun covers(s: Long, e: Long) = r.any { it[0] <= s && it[1] >= e }

    fun isComplete(size: Long) = size == 0L || covers(0, size)

    @Synchronized
    fun missing(size: Long): List<Pair<Long, Long>> {
        val gaps = ArrayList<Pair<Long, Long>>()
        var pos = 0L
        for (x in r) {
            if (x[0] >= size) break
            if (x[0] > pos) gaps.add(pos to x[0])
            pos = maxOf(pos, x[1])
        }
        if (pos < size) gaps.add(pos to size)
        return gaps
    }

    @Synchronized
    fun toList(): List<List<Long>> = r.map { listOf(it[0], it[1]) }
}

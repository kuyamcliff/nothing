package com.clipfx.share.core

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

object Proto {
    const val VERSION = 1
    const val TCP_PORT = 47630
    const val UDP_PORT = 47631
    const val MULTICAST_GROUP = "239.255.47.63"
}

@Serializable
data class DeviceInfo(val id: String, val name: String, val platform: String, val port: Int)

@Serializable
enum class ItemType { @SerialName("file") FILE, @SerialName("dir") DIR, @SerialName("text") TEXT }

/** One entry of a transfer. Path is relative and '/'-separated. */
@Serializable
data class ItemInfo(
    val i: Int,
    val path: String,
    val size: Long,
    val mtime: Long = 0,
    val type: ItemType = ItemType.FILE,
    val text: String? = null,
)

@Serializable
enum class TransferState {
    Waiting, Connecting, Active, Paused, PausedByPeer, Reconnecting, Completed, Declined, Cancelled, Failed, Interrupted;

    val isFinal get() = this == Completed || this == Declined || this == Cancelled || this == Failed
}

enum class Direction { Send, Receive }

@Serializable
data class ShareSettings(
    val deviceId: String = java.util.UUID.randomUUID().toString().replace("-", ""),
    val deviceName: String = "Android",
    val askBeforeReceive: Boolean = true,
    val trustedDevices: List<String> = emptyList(),
    val streams: Int = 4,
    val chunkMB: Int = 8,
    val verify: Boolean = true,
    val conflict: String = "rename", // rename | overwrite | skip
    val organizeByType: Boolean = true,
    val discoverable: Boolean = true,
    val port: Int = Proto.TCP_PORT,
    val theme: String = "system",
    val keepScreenOn: Boolean = false,
)

data class PeerView(
    val id: String, val name: String, val platform: String, val address: String, val port: Int,
    val online: Boolean, val trusted: Boolean, val manual: Boolean, val lastSeen: Long,
)

data class TransferView(
    val id: String, val direction: Direction, val peer: PeerView, val state: TransferState, val error: String?,
    val title: String, val files: Int, val folders: Int, val totalBytes: Long, val doneBytes: Long,
    val rate: Double, val eta: Double?, val currentFile: String?, val started: Long, val finished: Long?,
    val openPath: String?, val text: String?, val warning: String?, val preview: List<String>,
    val canPause: Boolean, val canResume: Boolean, val canCancel: Boolean, val canRetry: Boolean,
)

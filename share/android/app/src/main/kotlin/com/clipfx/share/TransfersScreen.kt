package com.clipfx.share

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import com.clipfx.share.core.*
import java.io.File

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TransfersScreen(activity: MainActivity, state: UiState) {
    val repo = activity.repo
    val active = state.transfers.filter { !it.state.isFinal }
    val history = state.transfers.filter { it.state.isFinal }
    var openFiles by remember { mutableStateOf<TransferView?>(null) }
    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("Transfers") },
            actions = { if (history.isNotEmpty()) TextButton(onClick = { repo.node.clearHistory() }) { Text("Clear history") } },
            colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background),
        )
        if (state.transfers.isEmpty()) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Icon(Icons.Outlined.SwapVert, null, Modifier.size(32.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    Spacer(Modifier.height(8.dp))
                    Text("No transfers yet", color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            return
        }
        LazyColumn(contentPadding = PaddingValues(16.dp, 4.dp, 16.dp, 16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            if (active.isNotEmpty()) item { SectionHeader("Active") {} }
            items(active, key = { it.id }) { TransferCard(activity, it) { openFiles = it } }
            if (history.isNotEmpty()) item { SectionHeader("History") {} }
            items(history, key = { it.id }) { TransferCard(activity, it) { openFiles = it } }
        }
    }
    openFiles?.let { t -> ReceivedFilesDialog(activity, t) { openFiles = null } }
}

fun stateLabel(t: TransferView): String = when (t.state) {
    TransferState.Waiting -> if (t.direction == Direction.Send) "Waiting for ${t.peer.name}" else "Waiting for you"
    TransferState.Connecting -> "Connecting"
    TransferState.Active -> if (t.direction == Direction.Send) "Sending" else "Receiving"
    TransferState.Paused -> "Paused"
    TransferState.PausedByPeer -> "Paused by ${t.peer.name}"
    TransferState.Reconnecting -> "Reconnecting"
    TransferState.Completed -> if (t.direction == Direction.Send) "Sent" else "Received"
    TransferState.Declined -> "Declined"
    TransferState.Cancelled -> "Cancelled"
    TransferState.Failed -> "Failed"
    TransferState.Interrupted -> "Interrupted"
}

@Composable
fun TransferCard(activity: MainActivity, t: TransferView, onOpen: (TransferView) -> Unit) {
    val node = activity.repo.node
    val bad = t.state == TransferState.Failed || t.state == TransferState.Declined || t.state == TransferState.Cancelled
    OutlinedCard(Modifier.fillMaxWidth(), colors = CardDefaults.outlinedCardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.padding(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(if (t.direction == Direction.Send) Icons.Outlined.ArrowUpward else Icons.Outlined.ArrowDownward, null,
                    Modifier.size(18.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.width(8.dp))
                Text(t.peer.name, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(stateLabel(t), style = MaterialTheme.typography.labelMedium,
                    color = when { bad -> MaterialTheme.colorScheme.error; t.state == TransferState.Completed -> Tone.ok(); else -> MaterialTheme.colorScheme.onSurfaceVariant })
            }
            Spacer(Modifier.height(6.dp))
            Text(t.title, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (!t.state.isFinal || t.state == TransferState.Completed) {
                Spacer(Modifier.height(10.dp))
                val frac = if (t.totalBytes > 0) t.doneBytes.toFloat() / t.totalBytes else if (t.state == TransferState.Completed) 1f else 0f
                LinearProgressIndicator(progress = { frac }, Modifier.fillMaxWidth().height(6.dp).clip(RoundedCornerShape(3.dp)),
                    color = MaterialTheme.colorScheme.primary, trackColor = MaterialTheme.colorScheme.surfaceVariant, drawStopIndicator = {})
                Spacer(Modifier.height(8.dp))
                Row {
                    Text("${Format.bytes(t.doneBytes)} of ${Format.bytes(t.totalBytes)}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f))
                    if (t.state == TransferState.Active) Text(listOfNotNull(Format.rate(t.rate), Format.eta(t.eta)).joinToString(" · "), style = MaterialTheme.typography.bodySmall)
                }
            }
            t.error?.takeIf { !it.isNullOrBlank() && t.state != TransferState.Completed }?.let {
                Spacer(Modifier.height(6.dp)); Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            }
            t.text?.let { text ->
                Spacer(Modifier.height(8.dp))
                Surface(color = MaterialTheme.colorScheme.surfaceVariant, shape = RoundedCornerShape(8.dp)) {
                    Text(text, Modifier.fillMaxWidth().padding(10.dp), style = MaterialTheme.typography.bodyMedium, maxLines = 6, overflow = TextOverflow.Ellipsis)
                }
            }
            Row(Modifier.fillMaxWidth().padding(top = 6.dp), horizontalArrangement = Arrangement.End) {
                if (t.text != null) TextButton(onClick = {
                    activity.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("text", t.text))
                }) { Text("Copy") }
                if (t.canPause) TextButton(onClick = { node.pause(t.id) }) { Icon(Icons.Outlined.Pause, null, Modifier.size(18.dp)); Spacer(Modifier.width(4.dp)); Text("Pause") }
                if (t.canResume) TextButton(onClick = { node.resume(t.id) }) { Icon(Icons.Outlined.PlayArrow, null, Modifier.size(18.dp)); Spacer(Modifier.width(4.dp)); Text("Resume") }
                if (t.canRetry) TextButton(onClick = { node.resume(t.id) }) { Text("Retry") }
                if (t.canCancel && t.state != TransferState.Waiting || (t.state == TransferState.Waiting && t.direction == Direction.Send))
                    TextButton(onClick = { node.cancel(t.id) }) { Text("Cancel") }
                if (t.state == TransferState.Completed && t.saved.isNotEmpty()) TextButton(onClick = { onOpen(t) }) { Text("Open") }
                if (t.state.isFinal || t.state == TransferState.Interrupted) IconButton(onClick = { node.remove(t.id) }) { Icon(Icons.Outlined.DeleteOutline, "Remove") }
            }
        }
    }
}

fun openFile(activity: MainActivity, location: String) {
    val uri = if (location.startsWith("content://")) Uri.parse(location)
    else FileProvider.getUriForFile(activity, "${activity.packageName}.files", File(location))
    val type = activity.contentResolver.getType(uri) ?: android.webkit.MimeTypeMap.getSingleton()
        .getMimeTypeFromExtension(location.substringAfterLast('.', "").lowercase()) ?: "*/*"
    val intent = Intent(Intent.ACTION_VIEW).setDataAndType(uri, type).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
    runCatching { activity.startActivity(Intent.createChooser(intent, null)) }.onFailure { activity.repo.message("No app can open this file.") }
}

@Composable
fun ReceivedFilesDialog(activity: MainActivity, t: TransferView, onDismiss: () -> Unit) {
    if (t.saved.size == 1) { LaunchedEffect(t.id) { openFile(activity, t.saved[0]); onDismiss() }; return }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(t.title, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        text = {
            LazyColumn(Modifier.heightIn(max = 420.dp)) {
                items(t.saved) { loc ->
                    val name = Uri.decode(loc.substringAfterLast('/')).substringAfterLast(':').substringAfterLast('/')
                    ListItem(
                        leadingContent = { KindIcon("file", name) },
                        headlineContent = { Text(name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                        modifier = Modifier.clip(RoundedCornerShape(8.dp)).then(Modifier),
                        colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surface),
                        trailingContent = { IconButton(onClick = { openFile(activity, loc) }) { Icon(Icons.Outlined.OpenInNew, "Open") } },
                    )
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Close") } },
    )
}

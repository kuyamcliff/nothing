package com.clipfx.share

import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.graphics.Bitmap
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.core.graphics.drawable.toBitmap
import com.clipfx.share.core.*
import com.google.zxing.BarcodeFormat
import com.google.zxing.EncodeHintType
import com.google.zxing.qrcode.QRCodeWriter
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

fun qrBitmap(text: String, size: Int, fg: Int, bg: Int): Bitmap {
    val m = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size, mapOf(EncodeHintType.MARGIN to 1))
    val px = IntArray(size * size) { if (m.get(it % size, it / size)) fg else bg }
    return Bitmap.createBitmap(px, size, size, Bitmap.Config.ARGB_8888)
}

@Composable
fun QrDialog(activity: MainActivity, state: UiState, onDismiss: () -> Unit) {
    val host = if (state.hotspot != null) state.addresses.firstOrNull { it.startsWith("192.168.") || it.startsWith("10.") } ?: state.addresses.firstOrNull()
    else state.addresses.firstOrNull()
    val code = host?.let { QrCode(it, state.port, state.selfName, state.hotspot?.ssid, state.hotspot?.password).encode() }
    // Always dark on white: scanners can't read inverted codes.
    val bmp = remember(code) { code?.let { qrBitmap(it, 640, android.graphics.Color.BLACK, android.graphics.Color.WHITE) } }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(state.selfName) },
        text = {
            Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.fillMaxWidth()) {
                if (bmp != null) Image(bmp.asImageBitmap(), "Code", Modifier.size(240.dp).clip(RoundedCornerShape(12.dp)))
                else Box(Modifier.size(240.dp), contentAlignment = Alignment.Center) { Text("Not connected", color = MaterialTheme.colorScheme.onSurfaceVariant) }
                Spacer(Modifier.height(12.dp))
                Text(host?.let { "$it:${state.port}" } ?: "", fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodyMedium)
                Spacer(Modifier.height(16.dp))
                ListItem(
                    leadingContent = { Icon(Icons.Outlined.WifiTethering, null) },
                    headlineContent = { Text("Private hotspot") },
                    supportingContent = state.hotspot?.let { h -> { Text(h.ssid, maxLines = 1, overflow = TextOverflow.Ellipsis) } },
                    trailingContent = { Switch(checked = state.hotspot != null, onCheckedChange = { if (it) activity.startHotspot() else activity.repo.stopHotspot() }) },
                    colors = ListItemDefaults.colors(containerColor = Color.Transparent),
                )
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Done") } },
    )
}

@Composable
fun AddDeviceDialog(activity: MainActivity, onDismiss: () -> Unit) {
    var address by remember { mutableStateOf("") }
    var busy by remember { mutableStateOf(false) }
    var failed by remember { mutableStateOf(false) }
    val repo = activity.repo
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Add by IP") },
        text = {
            OutlinedTextField(address, { address = it.trim(); failed = false }, singleLine = true, placeholder = { Text("192.168.43.1") },
                isError = failed, supportingContent = if (failed) ({ Text("Not reachable") }) else null)
        },
        confirmButton = {
            Button(onClick = {
                busy = true
                repo.io {
                    val p = repo.node.addManual(address)
                    busy = false
                    if (p == null) failed = true else { repo.selectedPeers.value = repo.selectedPeers.value + p.id; onDismiss() }
                }
            }, enabled = address.isNotBlank() && !busy) { if (busy) CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp) else Text("Add") }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

@Composable
fun OutlinedTextField(value: String, onValueChange: (String) -> Unit, singleLine: Boolean, placeholder: (@Composable () -> Unit)? = null,
                      isError: Boolean = false, supportingContent: (@Composable () -> Unit)? = null) {
    androidx.compose.material3.OutlinedTextField(value, onValueChange, singleLine = singleLine, placeholder = placeholder, isError = isError,
        supportingText = supportingContent, modifier = Modifier.fillMaxWidth())
}

@Composable
fun TextDialog(onDismiss: () -> Unit, onAdd: (String) -> Unit) {
    var text by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Text") },
        text = { androidx.compose.material3.OutlinedTextField(text, { text = it }, Modifier.fillMaxWidth().heightIn(min = 140.dp)) },
        confirmButton = { Button(onClick = { onAdd(text); onDismiss() }, enabled = text.isNotBlank()) { Text("Add") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

data class InstalledApp(val packageName: String, val label: String, val base: String, val splits: List<String>, val size: Long, val icon: Bitmap?)

@Composable
fun AppsDialog(activity: MainActivity, onDismiss: () -> Unit, onAdd: (List<InstalledApp>) -> Unit) {
    var apps by remember { mutableStateOf<List<InstalledApp>?>(null) }
    var picked by remember { mutableStateOf(setOf<String>()) }
    var query by remember { mutableStateOf("") }
    LaunchedEffect(Unit) {
        apps = withContext(Dispatchers.IO) {
            val pm = activity.packageManager
            @Suppress("DEPRECATION")
            pm.getInstalledApplications(PackageManager.GET_META_DATA)
                .filter { it.flags and ApplicationInfo.FLAG_SYSTEM == 0 || it.flags and ApplicationInfo.FLAG_UPDATED_SYSTEM_APP != 0 }
                .filter { it.packageName != activity.packageName }
                .map { ai ->
                    val splits = ai.splitSourceDirs?.toList() ?: emptyList()
                    InstalledApp(ai.packageName, pm.getApplicationLabel(ai).toString(), ai.sourceDir, splits,
                        (listOf(ai.sourceDir) + splits).sumOf { java.io.File(it).length() },
                        runCatching { pm.getApplicationIcon(ai).toBitmap(96, 96) }.getOrNull())
                }.sortedBy { it.label.lowercase() }
        }
    }
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
            Column {
                Row(Modifier.fillMaxWidth().padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
                    IconButton(onClick = onDismiss) { Icon(Icons.Outlined.Close, "Close") }
                    Text("Apps", style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
                    Button(onClick = { onAdd(apps.orEmpty().filter { it.packageName in picked }); onDismiss() }, enabled = picked.isNotEmpty()) {
                        Text(if (picked.isEmpty()) "Add" else "Add ${picked.size}")
                    }
                }
                androidx.compose.material3.OutlinedTextField(query, { query = it }, Modifier.fillMaxWidth().padding(horizontal = 16.dp),
                    singleLine = true, placeholder = { Text("Search") }, leadingIcon = { Icon(Icons.Outlined.Search, null) })
                val list = apps
                if (list == null) Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
                else LazyColumn(contentPadding = PaddingValues(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    items(list.filter { query.isBlank() || it.label.contains(query, true) }, key = { it.packageName }) { a ->
                        val on = a.packageName in picked
                        ListItem(
                            leadingContent = { a.icon?.let { Image(it.asImageBitmap(), null, Modifier.size(40.dp)) } ?: KindIcon("app", a.label) },
                            headlineContent = { Text(a.label, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                            supportingContent = { Text(Format.bytes(a.size)) },
                            trailingContent = { Checkbox(on, { picked = if (on) picked - a.packageName else picked + a.packageName }) },
                            modifier = Modifier.clip(RoundedCornerShape(12.dp)).clickable { picked = if (on) picked - a.packageName else picked + a.packageName },
                            colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surface),
                        )
                    }
                }
            }
        }
    }
}

@Composable
fun IncomingDialog(activity: MainActivity, t: TransferView) {
    val node = activity.repo.node
    var trust by remember(t.id) { mutableStateOf(false) }
    AlertDialog(
        onDismissRequest = {},
        icon = { PlatformIcon(t.peer.platform) },
        title = { Text(t.peer.name, textAlign = TextAlign.Center) },
        text = {
            Column(Modifier.fillMaxWidth()) {
                Text("${if (t.files == 1) "1 file" else "${t.files} files"} · ${Format.bytes(t.totalBytes)}", style = MaterialTheme.typography.titleSmall,
                    modifier = Modifier.fillMaxWidth(), textAlign = TextAlign.Center)
                Spacer(Modifier.height(12.dp))
                Surface(color = MaterialTheme.colorScheme.surfaceVariant, shape = RoundedCornerShape(10.dp)) {
                    Column(Modifier.fillMaxWidth().padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        t.preview.filter { it != "Text" || t.text == null }.forEach { Text(it, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium) }
                        if (t.text != null) Text(t.text!!, maxLines = 4, overflow = TextOverflow.Ellipsis)
                    }
                }
                t.warning?.let { Spacer(Modifier.height(10.dp)); Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
                Row(Modifier.fillMaxWidth().clickable { trust = !trust }.padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(trust, { trust = it })
                    Text("Always accept from ${t.peer.name}", style = MaterialTheme.typography.bodyMedium)
                }
            }
        },
        confirmButton = { Button(onClick = { node.accept(t.id, trust) }, enabled = t.warning == null || t.warning?.contains("FAT32") == false && t.warning?.contains("space") == false) { Text("Accept") } },
        dismissButton = { TextButton(onClick = { node.decline(t.id) }) { Text("Decline") } },
    )
}

@Composable
fun StoragePrompt(activity: MainActivity, onDismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        icon = { Icon(Icons.Outlined.FolderOpen, null) },
        title = { Text("Save to internal storage") },
        text = { Text("Allow access to all files so received files go to Internal storage/ClipFx Share.", textAlign = TextAlign.Center) },
        confirmButton = { Button(onClick = { activity.openAllFilesSettings(); onDismiss() }) { Text("Allow") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Not now") } },
    )
}

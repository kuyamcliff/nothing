package com.clipfx.share

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.clipfx.share.core.PeerView

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SendScreen(activity: MainActivity, state: UiState, onSent: () -> Unit) {
    val repo = activity.repo
    val selection by repo.selection.collectAsStateWithLifecycle()
    val selected by repo.selectedPeers.collectAsStateWithLifecycle()
    var showQr by remember { mutableStateOf(false) }
    var showAdd by remember { mutableStateOf(false) }
    var showText by remember { mutableStateOf(false) }
    var showApps by remember { mutableStateOf(false) }
    val canSend = selection.isNotEmpty() && selected.any { id -> state.peers.any { it.id == id } }

    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("ClipFx Share") },
            actions = {
                IconButton(onClick = { activity.scanQr() }) { Icon(Icons.Outlined.QrCodeScanner, "Scan code") }
            },
            colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background),
        )
        LazyColumn(Modifier.weight(1f), contentPadding = PaddingValues(16.dp, 4.dp, 16.dp, 16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            item { SelfCard(state) { showQr = true } }
            item {
                SectionHeader("Nearby") {
                    if (state.scanning) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                    else IconButton(onClick = { repo.scan() }) { Icon(Icons.Outlined.Refresh, "Scan network") }
                    IconButton(onClick = { showAdd = true }) { Icon(Icons.Outlined.Add, "Add by IP") }
                }
            }
            if (state.peers.isEmpty()) item { EmptyNearby(activity, state) }
            items(state.peers, key = { it.id }) { p ->
                PeerRow(p, p.id in selected) {
                    repo.selectedPeers.value = if (p.id in selected) selected - p.id else selected + p.id
                }
            }
            item { SectionHeader("Send") {} }
            item {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    PickButton(Icons.Outlined.Description, "Files", Modifier.weight(1f)) { activity.pickFiles.launch(arrayOf("*/*")) }
                    PickButton(Icons.Outlined.Image, "Media", Modifier.weight(1f)) {
                        activity.pickMedia.launch(androidx.activity.result.PickVisualMediaRequest(androidx.activity.result.contract.ActivityResultContracts.PickVisualMedia.ImageAndVideo))
                    }
                    PickButton(Icons.Outlined.Folder, "Folder", Modifier.weight(1f)) { activity.pickFolder.launch(null) }
                    PickButton(Icons.Outlined.Android, "Apps", Modifier.weight(1f)) { showApps = true }
                    PickButton(Icons.Outlined.TextFields, "Text", Modifier.weight(1f)) { showText = true }
                }
            }
            items(selection, key = { it.key }) { e ->
                ListItem(
                    leadingContent = { KindIcon(e.kind, e.name) },
                    headlineContent = { Text(e.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                    supportingContent = { Text(Format.bytes(e.size)) },
                    trailingContent = { IconButton(onClick = { repo.selection.value = selection - e }) { Icon(Icons.Outlined.Close, "Remove") } },
                    modifier = Modifier.clip(RoundedCornerShape(12.dp)),
                    colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surface),
                )
            }
        }
        Surface(color = MaterialTheme.colorScheme.background) {
            Row(Modifier.fillMaxWidth().padding(16.dp, 8.dp, 16.dp, 12.dp), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(if (selection.isEmpty()) "Nothing selected" else "${selection.size} selected", style = MaterialTheme.typography.titleSmall)
                    if (selection.isNotEmpty()) Text(Format.bytes(selection.sumOf { it.size }), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                if (selection.isNotEmpty()) TextButton(onClick = { repo.selection.value = emptyList() }) { Text("Clear") }
                Spacer(Modifier.width(8.dp))
                Button(onClick = { if (repo.send()) onSent() }, enabled = canSend, contentPadding = PaddingValues(24.dp, 12.dp)) {
                    Icon(Icons.Outlined.ArrowUpward, null, Modifier.size(18.dp)); Spacer(Modifier.width(8.dp)); Text("Send")
                }
            }
        }
    }
    if (showQr) QrDialog(activity, state) { showQr = false }
    if (showAdd) AddDeviceDialog(activity) { showAdd = false }
    if (showText) TextDialog(onDismiss = { showText = false }) { activity.addText(it) }
    if (showApps) AppsDialog(activity, onDismiss = { showApps = false }) { activity.addApps(it) }
}

@Composable
fun SectionHeader(title: String, actions: @Composable RowScope.() -> Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min = 40.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(title.uppercase(), style = SectionLabel, modifier = Modifier.weight(1f))
        actions()
    }
}

@Composable
fun SelfCard(state: UiState, onCode: () -> Unit) {
    OutlinedCard(Modifier.fillMaxWidth(), colors = CardDefaults.outlinedCardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(44.dp).clip(RoundedCornerShape(12.dp)).background(MaterialTheme.colorScheme.surfaceVariant), contentAlignment = Alignment.Center) {
                Icon(Icons.Outlined.PhoneAndroid, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f)) {
                Text(state.selfName, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(
                    when {
                        !state.ready -> "Starting"
                        state.hotspot != null -> "Hotspot ${state.hotspot.ssid}"
                        state.addresses.isEmpty() -> "Not connected"
                        else -> state.addresses.joinToString(" · ")
                    },
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Box(Modifier.size(8.dp).clip(CircleShape).background(if (state.ready && state.addresses.isNotEmpty()) Tone.ok() else MaterialTheme.colorScheme.outline))
            Spacer(Modifier.width(8.dp))
            FilledTonalIconButton(onClick = onCode) { Icon(Icons.Outlined.QrCode2, "My code") }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun EmptyNearby(activity: MainActivity, state: UiState) {
    OutlinedCard(Modifier.fillMaxWidth(), colors = CardDefaults.outlinedCardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.fillMaxWidth().padding(20.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(10.dp))
                Text("Searching", color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = { activity.scanQr() }) { Icon(Icons.Outlined.QrCodeScanner, null, Modifier.size(18.dp)); Spacer(Modifier.width(6.dp)); Text("Scan code", maxLines = 1) }
                OutlinedButton(onClick = { activity.startHotspot() }, enabled = state.hotspot == null) { Icon(Icons.Outlined.WifiTethering, null, Modifier.size(18.dp)); Spacer(Modifier.width(6.dp)); Text("Hotspot", maxLines = 1) }
            }
        }
    }
}

@Composable
fun PeerRow(p: PeerView, checked: Boolean, onClick: () -> Unit) {
    val border = if (checked) BorderStroke(1.5.dp, MaterialTheme.colorScheme.primary) else BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant)
    OutlinedCard(onClick = onClick, border = border, modifier = Modifier.fillMaxWidth(), colors = CardDefaults.outlinedCardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Row(Modifier.padding(horizontal = 14.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            PlatformIcon(p.platform)
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(p.name, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(p.address, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (p.trusted) Icon(Icons.Outlined.VerifiedUser, "Trusted", Modifier.size(16.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.width(8.dp))
            Box(Modifier.size(8.dp).clip(CircleShape).background(if (p.online) Tone.ok() else MaterialTheme.colorScheme.outline))
            Spacer(Modifier.width(12.dp))
            Checkbox(checked = checked, onCheckedChange = { onClick() })
        }
    }
}

@Composable
fun PlatformIcon(platform: String) {
    val icon = when (platform) { "windows" -> Icons.Outlined.Computer; "android" -> Icons.Outlined.PhoneAndroid; else -> Icons.Outlined.Devices }
    Box(Modifier.size(40.dp).clip(RoundedCornerShape(10.dp)).background(MaterialTheme.colorScheme.surfaceVariant), contentAlignment = Alignment.Center) {
        Icon(icon, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
fun KindIcon(kind: String, name: String) {
    val icon = when (kind) {
        "folder" -> Icons.Outlined.Folder
        "app" -> Icons.Outlined.Android
        "text" -> Icons.Outlined.TextFields
        else -> when (com.clipfx.share.core.Categories.of(name)) {
            "Videos" -> Icons.Outlined.Movie; "Images" -> Icons.Outlined.Image; "Music" -> Icons.Outlined.MusicNote
            "Documents" -> Icons.Outlined.Description; "Apps" -> Icons.Outlined.Android; "Archives" -> Icons.Outlined.FolderZip
            else -> Icons.Outlined.InsertDriveFile
        }
    }
    Box(Modifier.size(40.dp).clip(RoundedCornerShape(10.dp)).background(MaterialTheme.colorScheme.surfaceVariant), contentAlignment = Alignment.Center) {
        Icon(icon, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
fun PickButton(icon: ImageVector, label: String, modifier: Modifier, onClick: () -> Unit) {
    OutlinedCard(onClick = onClick, modifier = modifier, colors = CardDefaults.outlinedCardColors(containerColor = MaterialTheme.colorScheme.surface)) {
        Column(Modifier.fillMaxWidth().padding(vertical = 14.dp), horizontalAlignment = Alignment.CenterHorizontally) {
            Icon(icon, null, tint = MaterialTheme.colorScheme.onSurface)
            Spacer(Modifier.height(6.dp))
            Text(label, style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.SemiBold, maxLines = 1)
        }
    }
}

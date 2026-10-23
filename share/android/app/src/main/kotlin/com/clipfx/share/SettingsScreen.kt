package com.clipfx.share

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.clipfx.share.core.ShareSettings

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(activity: MainActivity, state: UiState) {
    val node = activity.repo.node
    val s = state.settings
    fun update(f: (ShareSettings) -> ShareSettings) { node.updateSettings(f); activity.repo.refresh() }
    var editName by remember { mutableStateOf(false) }
    var menu by remember { mutableStateOf<String?>(null) }

    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("Settings") }, colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background))
        Column(Modifier.verticalScroll(rememberScrollState()).padding(16.dp, 4.dp, 16.dp, 24.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Group("Device") {
                Row("Name", s.deviceName) { editName = true }
                Toggle("Visible to others", s.discoverable) { v -> update { it.copy(discoverable = v) } }
            }
            Group("Receiving") {
                Row("Save to", state.saveLocation) { menu = "save" }
                if (!state.allFiles && !state.saveLocation.startsWith("Internal storage/ClipFx")) Row("Internal storage access", "Off") { activity.openAllFilesSettings() }
                Toggle("Sort into folders by type", s.organizeByType) { v -> update { it.copy(organizeByType = v) } }
                Toggle("Ask before receiving", s.askBeforeReceive) { v -> update { it.copy(askBeforeReceive = v) } }
                Row("When a file exists", mapOf("rename" to "Keep both", "overwrite" to "Replace", "skip" to "Skip")[s.conflict] ?: s.conflict) { menu = "conflict" }
                if (s.trustedDevices.isNotEmpty()) Row("Trusted devices", "${s.trustedDevices.size}") { menu = "trusted" }
            }
            Group("Speed") {
                Row("Parallel connections", "${s.streams}") { menu = "streams" }
                Toggle("Verify every chunk", s.verify) { v -> update { it.copy(verify = v) } }
                Toggle("Keep screen on while transferring", s.keepScreenOn) { v -> update { it.copy(keepScreenOn = v) } }
            }
            Group("Appearance") {
                Row("Theme", mapOf("system" to "System", "light" to "Light", "dark" to "Dark")[s.theme] ?: "System") { menu = "theme" }
            }
            Text("ClipFx Share ${BuildConfig.VERSION_NAME}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(start = 4.dp))
        }
    }

    if (editName) NameDialog(s.deviceName, onDismiss = { editName = false }) { v -> update { it.copy(deviceName = v) } }
    when (menu) {
        "save" -> Choice("Save to", listOf("default" to "Internal storage/ClipFx Share", "pick" to "Choose folder…"), null, { menu = null }) {
            if (it == "default") { if (!state.allFiles) activity.openAllFilesSettings(); activity.repo.useDefaultLocation() } else activity.pickSaveFolder.launch(null)
        }
        "conflict" -> Choice("When a file exists", listOf("rename" to "Keep both", "overwrite" to "Replace", "skip" to "Skip"), s.conflict, { menu = null }) { v -> update { it.copy(conflict = v) } }
        "streams" -> Choice("Parallel connections", listOf(1, 2, 4, 6, 8, 12, 16).map { "$it" to "$it" }, "${s.streams}", { menu = null }) { v -> update { it.copy(streams = v.toInt()) } }
        "theme" -> Choice("Theme", listOf("system" to "System", "light" to "Light", "dark" to "Dark"), s.theme, { menu = null }) { v -> update { it.copy(theme = v) } }
        "trusted" -> {
            val names = state.peers.associate { it.id to it.name }
            Choice("Trusted devices", s.trustedDevices.map { it to (names[it] ?: it.take(8)) }, null, { menu = null }) { id -> node.setTrusted(id, false); activity.repo.refresh() }
        }
    }
}

@Composable
private fun Group(title: String, content: @Composable ColumnScope.() -> Unit) {
    Column {
        Text(title.uppercase(), style = SectionLabel, modifier = Modifier.padding(start = 4.dp, bottom = 8.dp))
        OutlinedCard(colors = CardDefaults.outlinedCardColors(containerColor = MaterialTheme.colorScheme.surface)) { Column(content = content) }
    }
}

@Composable
private fun Row(label: String, value: String, onClick: () -> Unit) {
    ListItem(
        headlineContent = { Text(label) },
        trailingContent = { Text(value, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 200.dp)) },
        modifier = Modifier.clickable(onClick = onClick),
        colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surface),
    )
}

@Composable
private fun Toggle(label: String, value: Boolean, onChange: (Boolean) -> Unit) {
    ListItem(
        headlineContent = { Text(label) },
        trailingContent = { Switch(checked = value, onCheckedChange = onChange) },
        modifier = Modifier.clickable { onChange(!value) },
        colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surface),
    )
}

@Composable
fun Choice(title: String, options: List<Pair<String, String>>, current: String?, onDismiss: () -> Unit, onPick: (String) -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            Column {
                options.forEach { (value, label) ->
                    Row(Modifier.fillMaxWidth().clickable { onPick(value); onDismiss() }.padding(vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                        if (current != null) RadioButton(selected = value == current, onClick = { onPick(value); onDismiss() })
                        Text(label, Modifier.padding(start = if (current != null) 4.dp else 0.dp))
                    }
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Close") } },
    )
}

@Composable
fun NameDialog(current: String, onDismiss: () -> Unit, onSave: (String) -> Unit) {
    var v by remember { mutableStateOf(current) }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Name") },
        text = { OutlinedTextField(v, { v = it.take(40) }, singleLine = true) },
        confirmButton = { Button(onClick = { onSave(v); onDismiss() }, enabled = v.isNotBlank()) { Text("Save") } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

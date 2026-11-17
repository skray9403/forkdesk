// TwinDesk — Copyright (C) 2026 inukaasith. GPL-3.0, see LICENSE.
// History's own view (History.kt): the clipboard's or the notifications', newest first, with a
// filter; a copied item is copied again with a tap. And the History card on the Clipboard and
// Notifications pages: Keep history, for how long, Open, and a shortcut on the Overview.
package com.twindesk.android

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.TextButton
import androidx.compose.material3.Icon
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.text.DateFormat
import java.util.Date

private fun ago(at: Long): String {
    val s = (System.currentTimeMillis() - at) / 1000
    return when {
        s < 60 -> tr("Just now")
        s < 3600 -> tr("{count} min ago", "count" to (s / 60))
        s < 86400 -> DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(at))
        else -> DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.SHORT).format(Date(at))
    }
}

@Composable
fun HistoryScreen(core: Core, v: Int, what: String) {
    @Suppress("UNUSED_EXPRESSION") v
    val clip = what == "clip"
    val name = core.host?.name ?: tr("the computer")
    var q by rememberSaveable { mutableStateOf("") }
    val close = { History.view.value = null }
    androidx.activity.compose.BackHandler { close() }
    val all = if (clip) core.history.clip() else core.history.notifs()
    val list = if (q.isBlank()) all else all.filter { e -> listOf(e.text, e.title, e.app).any { it.contains(q.trim(), ignoreCase = true) } }
    Column(Modifier.fillMaxSize().safeDrawingPadding()) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(onClick = close) { Icon(Ico.left, contentDescription = tr("Back")) }
            Text(
                if (clip) tr("Clipboard history") else tr("Notification history"),
                style = MaterialTheme.typography.titleLarge,
                modifier = Modifier.weight(1f),
            )
            if (all.isNotEmpty()) {
                TextButton(onClick = { core.history.clear(what) }) { Text(tr("Clear")) }
            }
        }
        Text(
            if (core.history.save) tr("Kept encrypted on this device for {count} days", "count" to core.history.days)
            else tr("Since TwinDesk opened. Keep history on its page to keep it longer"),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(horizontal = 20.dp, vertical = 4.dp),
        )
        OutlinedTextField(
            value = q,
            onValueChange = { q = it },
            singleLine = true,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
            placeholder = { Text(tr("Filter")) },
            leadingIcon = { Icon(Ico.search, contentDescription = null) },
        )
        if (list.isEmpty()) {
            Text(
                if (all.isEmpty()) tr("Nothing yet") else tr("Nothing found"),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(24.dp),
            )
        }
        LazyColumn(Modifier.fillMaxWidth().weight(1f).padding(horizontal = 8.dp, vertical = 4.dp)) {
            itemsIndexed(list, key = { i, e -> "$i:${e.at}" }) { i, e ->
                val title = if (clip) e.text.replace('\n', ' ') else {
                    listOf(e.app, e.title).filter { it.isNotEmpty() }.joinToString(" · ")
                }
                ListItem(
                    headlineContent = {
                        Text(title, maxLines = if (clip) 3 else 1, overflow = TextOverflow.Ellipsis)
                    },
                    supportingContent = {
                        Column {
                            if (!clip && e.text.isNotEmpty()) {
                                Text(e.text.replace('\n', ' '), maxLines = 2, overflow = TextOverflow.Ellipsis)
                            }
                            Text(
                                (if (clip) (if (e.dir == "in") tr("From {name}", "name" to name) else tr("To {name}", "name" to name)) + " · " else "") +
                                    ago(e.at) + if (clip) tr(" · tap to copy") else "",
                                style = MaterialTheme.typography.labelSmall,
                            )
                        }
                    },
                    trailingContent = if (clip) {
                        { Icon(Ico.clipboard, contentDescription = tr("Copy again")) }
                    } else null,
                    modifier = Modifier.fillMaxWidth().then(
                        if (clip) Modifier.clickable { core.clip.copyAgain(Clip.Item(e.dir, e.text)) } else Modifier
                    ),
                    colors = ListItemDefaults.colors(containerColor = Color.Transparent),
                )
                if (i < list.lastIndex) HorizontalDivider(Modifier.padding(start = 16.dp))
            }
        }
    }
}

// The History card on the Clipboard and Notifications pages.
@Composable
fun HistoryCard(core: Core, what: String) {
    val h = core.history
    val tile = if (what == "clip") "clipHistory" else "notifHistory"
    var pinned by remember { mutableStateOf(Tiles.has(core.store, tile)) }
    MacCard(tr("History")) {
        MacRow(
            if (what == "clip") tr("Clipboard history") else tr("Notification history"),
            tr("{count} items", "count" to (if (what == "clip") h.clip() else h.notifs()).size),
            first = true,
        ) {
            FilledTonalButton(onClick = { History.view.value = what }) {
                Text(tr("Open"))
            }
        }
        SwitchRow(tr("Keep history"), if (h.save) tr("Encrypted on this device, with a key only TwinDesk can use") else tr("Off: forgotten when TwinDesk quits"), h.save) { h.setSave(it) }
        if (h.save) {
            MacRow(tr("Keep for"), tr("Older items are deleted"))
            val opts = listOf(1 to tr("A day"), 7 to tr("A week"), 30 to tr("30 days"), 365 to tr("A year"))
            SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth().padding(start = 16.dp, end = 16.dp, bottom = 12.dp)) {
                opts.forEachIndexed { i, (d, label) ->
                    SegmentedButton(selected = h.days == d, onClick = { h.setDays(d) }, shape = SegmentedButtonDefaults.itemShape(i, opts.size)) { Text(label, fontSize = 12.sp, maxLines = 1) }
                }
            }
        }
        if (h.error.isNotEmpty()) MacRow(h.error, "")
        SwitchRow(tr("Shortcut on the Overview"), tr("Opens this history"), pinned) { Tiles.set(core.store, tile, it); pinned = it; core.changed() }
    }
}

// TwinDesk — Copyright (C) 2026 inukaasith. GPL-3.0, see LICENSE.
// Search: the settings, shortcuts and features, found by what they do ("share pc audio to phone",
// "phone audio on pc", "dark mode"), and the pages by name. Opened from the round button at the
// bottom bar's right end, or More's search field. The entries and words are the computers'
// (shared/search-index.js → SearchIndex.kt); the matcher is the same as theirs.
package com.twindesk.android

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Icon
import androidx.compose.ui.graphics.Color
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.TextButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.text.Normalizer

object Search {
    val open = mutableStateOf(false)

    // One thing to find: an entry (SearchIndex) or a page. en: its English name; title: as shown.
    class Item(val en: String, val title: String, val k: String, val page: String, val dir: String, val where: String, val isPage: Boolean)

    private class Tok(val raw: String, val w: String)

    private val SPLIT = Regex("[^\\p{L}\\p{N}]+")
    private val MARKS = Regex("\\p{Mn}+")

    // Words: lower case, letters and digits, one word for synonyms, a plural's s gone.
    private fun tokens(s: String, keepStop: Boolean = false): List<Tok> =
        Normalizer.normalize(s.lowercase(), Normalizer.Form.NFKD).replace(MARKS, "").split(SPLIT)
            .filter { it.isNotEmpty() && (keepStop || it !in SearchIndex.STOP) }
            .map { raw ->
                var w = SearchIndex.SYN[raw] ?: raw
                if (w.length > 3 && w.endsWith("s") && !w.endsWith("ss")) w = w.dropLast(1)
                Tok(raw, SearchIndex.SYN[w] ?: w)
            }

    // Which way the query goes: "c2p" (pc audio to phone), "p2c" (phone audio on pc), or "".
    private fun direction(q: String): String {
        val ws = tokens(q, true).map { it.w }
        val devs = ws.mapIndexedNotNull { i, w ->
            val d = when (w) { "computer" -> 'c'; "phone" -> 'p'; else -> return@mapIndexedNotNull null }
            d to (i > 0 && ws[i - 1] in SearchIndex.TO)
        }
        if (devs.isEmpty()) return ""
        if (devs.size == 1) {
            val (d, to) = devs[0]
            return if (to) (if (d == 'p') "c2p" else "p2c") else (if (d == 'p') "p2c" else "c2p")
        }
        val a = devs[0].first
        if (devs.none { it.first != a }) return ""
        return if (a == 'c') "c2p" else "p2c"
    }

    fun match(q: String, items: List<Item>): List<Item> {
        val qt = tokens(q).distinctBy { it.w }
        if (qt.isEmpty()) return emptyList()
        val qs = qt.map { it.w }
        val dir = direction(q)
        return items.mapIndexedNotNull { i, e ->
            val tk = tokens(e.en) + tokens(e.k) + tokens(e.title) + tokens(e.where)
            val bag = tk.map { it.w }.toSet()
            val all = bag + tk.map { it.raw }
            var s = 0.0
            var most = 0.0
            for (t in qt) {
                val wt = if (t.w in SearchIndex.LIGHT) 0.4 else 1.0
                most += wt
                if (t.w in bag) s += wt
                else if (t.raw.length >= 2 && all.any { it.startsWith(t.raw) || it.startsWith(t.w) }) s += 0.7 * wt
                else if (t.w.length >= 4 && all.any { it.length >= 4 && t.w.startsWith(it) }) s += 0.5 * wt
            }
            var score = s / most
            if (score < (if (qs.size >= 3) 0.6 else 0.5)) return@mapIndexedNotNull null
            if (dir.isNotEmpty() && e.dir.isNotEmpty()) score += if (e.dir == dir) 0.3 else -0.3
            if (tokens(e.title).joinToString(" ") { it.w }.startsWith(qs.joinToString(" "))) score += 0.2
            Triple(e, score, i)
        }.sortedWith(compareByDescending<Triple<Item, Double, Int>> { it.second }.thenBy { it.third }).map { it.first }
    }

    // The pages shown now (Tab name to its English title) and the entries on them.
    fun items(pages: List<Pair<String, String>>): List<Item> {
        val names = pages.toMap()
        return pages.map { (id, en) -> Item(en, tr(en), id, id, "", "", true) } +
            (SearchIndex.ENTRIES + SearchIndex.SETTINGS).filter { it.page in names }.map { Item(it.en, it.title(), it.k, it.page, it.dir, tr(names[it.page] ?: ""), false) }
    }
}

// The search page, over everything: a native Material input followed by standard list results.
@Composable
fun SearchScreen(pages: List<Pair<String, String>>, go: (String) -> Unit) {
    var q by rememberSaveable { mutableStateOf("") }
    val items = remember(pages) { Search.items(pages) }
    val found = remember(q, items) { Search.match(q, items).take(20) }
    val focus = remember { FocusRequester() }
    val close = { Search.open.value = false }
    androidx.activity.compose.BackHandler { close() }
    LaunchedEffect(Unit) { runCatching { focus.requestFocus() } }

    Column(Modifier.fillMaxSize().safeDrawingPadding().padding(horizontal = 16.dp)) {
        Row(
            Modifier.fillMaxWidth().padding(top = 12.dp, bottom = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            OutlinedTextField(
                value = q,
                onValueChange = { q = it },
                modifier = Modifier.weight(1f).focusRequester(focus),
                singleLine = true,
                placeholder = { Text(tr("Search settings and features"), maxLines = 1, overflow = TextOverflow.Ellipsis) },
                leadingIcon = { Icon(Ico.search, contentDescription = null) },
                trailingIcon = {
                    if (q.isNotEmpty()) {
                        IconButton(onClick = { q = "" }) {
                            Icon(Ico.close, contentDescription = tr("Clear"))
                        }
                    }
                },
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(onSearch = { found.firstOrNull()?.let { close(); go(it.page) } }),
            )
            TextButton(onClick = close, modifier = Modifier.padding(start = 4.dp)) {
                Text(tr("Cancel"))
            }
        }

        if (found.isEmpty()) {
            Text(
                if (q.isBlank()) tr("Try “share pc audio to phone”, “dark mode” or “send a file”") else tr("Nothing found"),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(horizontal = 8.dp, vertical = 16.dp),
            )
        }

        LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
            itemsIndexed(found) { index, item ->
                ListItem(
                    content = {
                        Text(item.title, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    },
                    supportingContent = {
                        Text(if (item.isPage) tr("Page") else item.where, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    },
                    trailingContent = {
                        Icon(Ico.right, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    },
                    modifier = Modifier.clickable { close(); go(item.page) },
                    colors = ListItemDefaults.colors(containerColor = Color.Transparent),
                )
                if (index < found.lastIndex) HorizontalDivider(Modifier.padding(start = 16.dp))
            }
        }
    }
}
// More's search field: a read-only Material text field opens the full search page.
@Composable
fun SearchField() {
    OutlinedTextField(
        value = "",
        onValueChange = {},
        readOnly = true,
        singleLine = true,
        shape = CircleShape,
        colors = OutlinedTextFieldDefaults.colors(
            unfocusedContainerColor = MaterialTheme.colorScheme.surfaceContainerLow,
            focusedContainerColor = MaterialTheme.colorScheme.surfaceContainerLow,
            unfocusedBorderColor = MaterialTheme.colorScheme.outlineVariant,
            focusedBorderColor = MaterialTheme.colorScheme.primary,
        ),
        modifier = Modifier.fillMaxWidth().padding(bottom = 14.dp).clickable { Search.open.value = true },
        leadingIcon = { Icon(Ico.search, contentDescription = null) },
        placeholder = { Text(tr("Search settings and features")) },
    )
}

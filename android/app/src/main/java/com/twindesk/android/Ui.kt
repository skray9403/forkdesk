// TwinDesk — Copyright (C) 2026 inukaasith. GPL-3.0, see LICENSE.
// Shared Material 3 presentation primitives for feature pages, navigation and settings.
@file:OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
package com.twindesk.android

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.FilledTonalButton

import androidx.compose.material3.Badge
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.NavigationDrawerItem
import androidx.compose.material3.NavigationDrawerItemDefaults
import androidx.compose.material3.RadioButton
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.MaterialExpressiveTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.layout.Layout
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

// ---- colours (mac/control.css :root) ----

data class Palette(
    val accent: Color, val accentSoft: Color, val content: Color, val sidebar: Color, val card: Color, val line: Color,
    val text: Color, val muted: Color, val faint: Color, val good: Color, val warn: Color, val bad: Color, val trackOff: Color, val hover: Color,
    val chip: Color = Color.Transparent, // inner containers and tonal buttons (the phone's look)
    val sheet: Color = Color.Transparent, // the phone: the big rounded container the pages sit in
)

private val Light = Palette(
    accent = Color(0xFF0A84FF), accentSoft = Color(0x1F0A84FF), content = Color(0xFFF2F2F5), sidebar = Color(0xFFE8E8ED), card = Color.White,
    line = Color(0x14000000), text = Color(0xFF1D1D1F), muted = Color(0xFF6E6E73), faint = Color(0xFFA1A1A6),
    good = Color(0xFF30B94D), warn = Color(0xFFE8A10C), bad = Color(0xFFE5373A), trackOff = Color(0x1F000000), hover = Color(0x0D000000),
)
private val Dark = Palette(
    accent = Color(0xFF0A84FF), accentSoft = Color(0x380A84FF), content = Color(0xFF1E1E1F), sidebar = Color(0xFF2B2B2D), card = Color(0xFF2A2A2C),
    line = Color(0x12FFFFFF), text = Color(0xFFF5F5F7), muted = Color(0xFF98989D), faint = Color(0xFF6C6C70),
    good = Color(0xFF32D74B), warn = Color(0xFFFFB340), bad = Color(0xFFFF453A), trackOff = Color(0x29FFFFFF), hover = Color(0x0FFFFFFF),
)

val LocalPalette = staticCompositionLocalOf { Light }
val pal: Palette @Composable get() = LocalPalette.current

// The look (General › Appearance, as on the computers: shared/look.js): the theme (system, light,
// dark) and the accent colour, "" following the phone's own (Material You, Android 12+; else blue).
// A local preference, never synchronized.
object Look {
    val THEMES = listOf("system", "light", "dark")
    val ACCENTS = listOf(0xFF0A84FF, 0xFF5E5CE6, 0xFFBF5AF2, 0xFFFF375F, 0xFFFF453A, 0xFFFF9F0A, 0xFF30D158, 0xFF00C7BE, 0xFF8E8E93)
    private const val FALLBACK = 0xFF0A84FF
    var theme by androidx.compose.runtime.mutableStateOf("system")
    var accent by androidx.compose.runtime.mutableStateOf(0L) // 0: the system's
    private var prefs: android.content.SharedPreferences? = null

    fun load(ctx: android.content.Context) {
        if (prefs != null) return
        val p = ctx.applicationContext.getSharedPreferences("look", android.content.Context.MODE_PRIVATE)
        prefs = p
        theme = p.getString("theme", "system").takeIf { it in THEMES } ?: "system"
        accent = p.getLong("accent", 0L)
    }
    fun pickTheme(v: String) { theme = if (v in THEMES) v else "system"; prefs?.edit()?.putString("theme", theme)?.apply() }
    fun pickAccent(v: Long) { accent = v; prefs?.edit()?.putLong("accent", v)?.apply() }

    // The device's wallpaper-derived accent for "System".
    fun system(ctx: android.content.Context, dark: Boolean): Color =
        if (android.os.Build.VERSION.SDK_INT >= 31) {
            (if (dark) androidx.compose.material3.dynamicDarkColorScheme(ctx) else androidx.compose.material3.dynamicLightColorScheme(ctx)).primary
        } else Color(FALLBACK)
}

@Composable
fun Theme(content: @Composable () -> Unit) {
    val ctx = androidx.compose.ui.platform.LocalContext.current
    Look.load(ctx)
    val dark = when (Look.theme) {
        "light" -> false
        "dark" -> true
        else -> isSystemInDarkTheme()
    }

    // Start with Android's wallpaper-derived Material You scheme. Keep the existing optional
    // accent override, but leave neutral surfaces, text, outlines and elevation to Material 3.
    val baseScheme = if (android.os.Build.VERSION.SDK_INT >= 31) {
        if (dark) androidx.compose.material3.dynamicDarkColorScheme(ctx)
        else androidx.compose.material3.dynamicLightColorScheme(ctx)
    } else {
        if (dark) darkColorScheme() else lightColorScheme()
    }
    val accent = if (Look.accent != 0L) Color(Look.accent) else baseScheme.primary
    val scheme = if (Look.accent == 0L) baseScheme else baseScheme.copy(
        primary = accent,
        onPrimary = if (accent.luminance() > 0.5f) Color.Black else Color.White,
        primaryContainer = accent.copy(alpha = if (dark) 0.30f else 0.14f),
        onPrimaryContainer = accent,
    )

    val p = Palette(
        accent = scheme.primary,
        accentSoft = scheme.primaryContainer,
        content = scheme.background,
        sidebar = scheme.surfaceContainerLow,
        card = scheme.surfaceContainerLow,
        line = scheme.outlineVariant,
        text = scheme.onSurface,
        muted = scheme.onSurfaceVariant,
        faint = scheme.outline,
        good = scheme.tertiary,
        warn = scheme.secondary,
        bad = scheme.error,
        trackOff = scheme.surfaceContainerHigh,
        hover = scheme.surfaceVariant,
        chip = scheme.secondaryContainer,
        sheet = scheme.surface,
    )

    CompositionLocalProvider(LocalPalette provides p) {
        MaterialExpressiveTheme(colorScheme = scheme, content = content)
    }
}

// ---- icons: mac/control.js ICONS, the same 20×20 line drawings ----

private fun lineIcon(name: String, d: String): ImageVector = ImageVector.Builder(name, 24.dp, 24.dp, 20f, 20f).apply {
    addPath(
        pathData = PathParser().parsePathString(d).toNodes(), fill = null, stroke = SolidColor(Color.Black),
        strokeLineWidth = 1.8f, strokeLineCap = StrokeCap.Round, strokeLineJoin = StrokeJoin.Round,
    )
}.build()

object Ico {
    val overview = lineIcon("overview", "M3 9.5 10 4l7 5.5V17H3z M8 17v-4h4v4")
    val link = lineIcon("link", "M8.5 11.5a3.2 3.2 0 0 0 4.5 0l2.5-2.5a3.2 3.2 0 0 0-4.5-4.5l-.8.8 M11.5 8.5a3.2 3.2 0 0 0-4.5 0L4.5 11a3.2 3.2 0 0 0 4.5 4.5l.8-.8")
    val display = lineIcon("display", "M3 4h14v9H3z M7 17h6 M10 13v4")
    val cursor = lineIcon("cursor", "M5 3l10 6-4.5 1.2L8.5 15z")
    val keyboard = lineIcon("keyboard", "M2.5 6h15v8h-15z M5.5 9h1 M8.5 9h1 M11.5 9h1 M14.5 9h0 M6.5 11.5h7")
    val rezume = lineIcon("rezume", "M2 4.5h8v6H2z M1 12.5h10 M13.5 6.5h4.5v10h-4.5z M15.2 14.5h1.1 M3.5 16h6 M8 14.4l1.6 1.6L8 17.6")
    val deck = lineIcon("deck", "M3 3h6v6H3z M11 3h6v6h-6z M3 11h6v6H3z M11 11h6v6h-6z")
    val remote = lineIcon("remote", "M6.5 6.5h7a4 4 0 0 1 4 4v1.5a2.8 2.8 0 0 1-5 1.7L11.4 12H8.6l-1.1 1.7a2.8 2.8 0 0 1-5-1.7v-1.5a4 4 0 0 1 4-4z M6 8.5v3 M4.5 10h3 M13 9.5h0 M14.5 11h0")
    val clipboard = lineIcon("clipboard", "M6 4h8v13H6z M8 3h4v2.5H8z M8 9h4 M8 12h4")
    val files = lineIcon("files", "M3 5h5l1.5 2H17v9H3z")
    val audio = lineIcon("audio", "M4 8h3l4-3v10l-4-3H4z M14 7.5c1 .8 1 4.2 0 5 M16 5.5c2.2 1.8 2.2 7.2 0 9")
    val general = lineIcon("general", "M10 7a3 3 0 1 0 0 6 3 3 0 0 0 0-6z M10 2.5v2 M10 15.5v2 M2.5 10h2 M15.5 10h2 M4.7 4.7l1.4 1.4 M13.9 13.9l1.4 1.4 M4.7 15.3l1.4-1.4 M13.9 6.1l1.4-1.4")
    val about = lineIcon("about", "M10 2.5a7.5 7.5 0 1 0 0 15 7.5 7.5 0 0 0 0-15z M10 9v5 M10 6.2v.1")
    val bell = lineIcon("bell", "M5.5 13.5V9a4.5 4.5 0 0 1 9 0v4.5l1.5 1.5h-12z M8.5 16.5a1.5 1.5 0 0 0 3 0")
    val phone = lineIcon("phone", "M6.5 2.5h7a1 1 0 0 1 1 1v13a1 1 0 0 1-1 1h-7a1 1 0 0 1-1-1v-13a1 1 0 0 1 1-1z M9 15h2")
    val more = lineIcon("more", "M4.5 10h.1 M10 10h.1 M15.5 10h.1")
    val call = lineIcon("call", "M6 3h2.5l1.2 3.2-1.7 1.3a9 9 0 0 0 4.5 4.5l1.3-1.7 3.2 1.2V16a1 1 0 0 1-1 1A13 13 0 0 1 3 4a1 1 0 0 1 1-1z")
    val message = lineIcon("message", "M3 4.5h14v9H8.5L5 16.5v-3H3z")
    val camera = lineIcon("camera", "M2.5 6h10v8h-10z M12.5 8.5l5-2.5v8l-5-2.5")
    val photo = lineIcon("photo", "M3 4.5h14v11H3z M3 13l4-4 3.5 3.5 2-2L17 14.5 M12.5 7.5h.1")
    val folder = lineIcon("folder", "M2.5 5h5.5l1.5 2h8v9.5h-15z")
    val media = lineIcon("media", "M7.5 14.5V4.5l9-2v10 M7.5 14.5a2.2 2.2 0 1 1-4.4 0 2.2 2.2 0 0 1 4.4 0z M16.5 12.5a2.2 2.2 0 1 1-4.4 0 2.2 2.2 0 0 1 4.4 0z")
    val play = lineIcon("play", "M6 4l10 6-10 6z")
    val pause = lineIcon("pause", "M7 4.5v11 M13 4.5v11")
    val prev = lineIcon("prev", "M4.5 4.5v11 M16 4.5l-8 5.5 8 5.5z")
    val next = lineIcon("next", "M15.5 4.5v11 M4 4.5l8 5.5-8 5.5z")
    // The Overview's shortcuts (Shortcuts.kt).
    val mic = lineIcon("mic", "M10 2.5a2.5 2.5 0 0 1 2.5 2.5v5a2.5 2.5 0 0 1-5 0V5A2.5 2.5 0 0 1 10 2.5z M5 9.5a5 5 0 0 0 10 0 M10 14.5v3 M7.5 17.5h5")
    val touchpad = lineIcon("touchpad", "M3 4h14v9.5H3z M3 13.5h14v2.5a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z M10 13.5V17")
    val gamepad = lineIcon("gamepad", "M6.5 6.5h7a4 4 0 0 1 4 4v1.5a2.8 2.8 0 0 1-5 1.7L11.4 12H8.6l-1.1 1.7a2.8 2.8 0 0 1-5-1.7v-1.5a4 4 0 0 1 4-4z M6 8.5v3 M4.5 10h3 M13 9.5h0 M14.5 11h0")
    // A phone pointing at a screen, and a slide with a pointer.
    val air = lineIcon("air", "M7 6.5h4.5v10H7z M8.5 14.5h1.5 M13.5 5.5a3 3 0 0 1 0 4 M15.5 3.5a6 6 0 0 1 0 8")
    val present = lineIcon("present", "M2.5 3.5h15v9.5h-15z M10 13v3.5 M7 17h6 M6 10l3-3 2 2 3-3")
    val send = lineIcon("send", "M3 5h5l1.5 2H17v9H3z M10 14.5v-5 M7.8 11.5 10 9.3l2.2 2.2")
    val lock = lineIcon("lock", "M5 9h10v8H5z M7 9V6.5a3 3 0 0 1 6 0V9 M10 12v2")
    val sleep = lineIcon("sleep", "M15.5 12.5A6.5 6.5 0 0 1 7.5 4.5a6.5 6.5 0 1 0 8 8z")
    val dnd = lineIcon("dnd", "M10 2.5a7.5 7.5 0 1 0 0 15 7.5 7.5 0 0 0 0-15z M6.5 10h7")
    val bluetooth = lineIcon("bluetooth", "M6 6.5l8 7-4 3.5V3l4 3.5-8 7")
    val pencil = lineIcon("pencil", "M4 16l1-4 8.5-8.5 3 3L8 15z M11.5 5.5l3 3")
    val plus = lineIcon("plus", "M10 4.5v11 M4.5 10h11")
    val minus = lineIcon("minus", "M5 10h10")
    val close = lineIcon("close", "M5 5l10 10 M15 5 5 15")
    // Clipboard and notification history: a clock with a list.
    val search = lineIcon("search", "M8.5 3.5a5 5 0 1 1 0 10a5 5 0 1 1 0-10z M12.2 12.2 16.5 16.5")
    val history = lineIcon("history", "M10 3a7 7 0 1 1-6.6 4.7 M3 3.5v4h4 M10 6.5V10l2.5 2")
    val left = lineIcon("left", "M12.5 4.5 7 10l5.5 5.5")
    val right = lineIcon("right", "M7.5 4.5 13 10l-5.5 5.5")
    val shield = lineIcon("shield", "M10 2.5l6 2.5v4.5c0 4-2.7 6.7-6 8-3.3-1.3-6-4-6-8V5z M7.5 10l2 2 3.5-3.5")
}

// Feature glyphs remain recognizable, but navigation no longer wraps them in custom gradient tiles.
@Composable
fun Tile(icon: ImageVector, colors: List<Color>, size: Int = 26) {
    Icon(
        imageVector = icon,
        contentDescription = null,
        tint = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.size((size * 0.72f).dp),
    )
}

@Composable
// The dark icon in the dark theme, the colourful one in the light theme.
fun Logo(size: Int) = Image(painterResource(if (pal.text.luminance() > 0.5f) R.drawable.twindesk_logo_dark else R.drawable.twindesk_logo_light), "TwinDesk", Modifier.size(size.dp))

@Composable
fun Dot(color: Color, size: Int = 8) {
    Badge(modifier = Modifier.size(size.dp), containerColor = color)
}

// Use the stock Material navigation-drawer item instead of a hand-built pill row.
@Composable
fun NavItem(
    icon: ImageVector,
    colors: List<Color>,
    label: String,
    selected: Boolean,
    on: Boolean,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    NavigationDrawerItem(
        label = { Text(label, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        selected = selected,
        onClick = { if (enabled) onClick() },
        modifier = Modifier.fillMaxWidth().padding(vertical = 2.dp).alpha(if (enabled) 1f else 0.38f),
        icon = { Icon(icon, contentDescription = null) },
        badge = { if (on) Badge(containerColor = pal.good) },
        colors = NavigationDrawerItemDefaults.colors(
            selectedContainerColor = MaterialTheme.colorScheme.secondaryContainer,
            selectedTextColor = MaterialTheme.colorScheme.onSecondaryContainer,
            selectedIconColor = MaterialTheme.colorScheme.onSecondaryContainer,
            unselectedContainerColor = Color.Transparent,
        ),
    )
}

// ---- page parts ----

// General › Appearance: theme and optional accent override. System uses wallpaper-derived colours.
@Composable
fun AppearanceCard() {
    val ctx = androidx.compose.ui.platform.LocalContext.current
    val themeOptions = listOf("system" to tr("System"), "light" to tr("Light"), "dark" to tr("Dark"))
    val accentOptions = listOf(
        0L to tr("System"),
        Look.ACCENTS[0] to tr("Blue"),
        Look.ACCENTS[1] to tr("Indigo"),
        Look.ACCENTS[2] to tr("Purple"),
        Look.ACCENTS[3] to tr("Pink"),
        Look.ACCENTS[4] to tr("Red"),
        Look.ACCENTS[5] to tr("Orange"),
        Look.ACCENTS[6] to tr("Green"),
        Look.ACCENTS[7] to tr("Teal"),
        Look.ACCENTS[8] to tr("Grey"),
    )

    MacCard(tr("Appearance")) {
        Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(tr("Theme"), style = MaterialTheme.typography.titleSmall)
            SingleChoiceSegmentedButtonRow(modifier = Modifier.fillMaxWidth()) {
                themeOptions.forEachIndexed { index, option ->
                    SegmentedButton(
                        selected = Look.theme == option.first,
                        onClick = { Look.pickTheme(option.first) },
                        shape = SegmentedButtonDefaults.itemShape(index = index, count = themeOptions.size),
                    ) { Text(option.second, maxLines = 1) }
                }
            }
            Text(tr("Accent colour"), style = MaterialTheme.typography.titleSmall)
            Text(
                tr("System uses this phone’s wallpaper colours"),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Row(
                Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                accentOptions.forEach { (value, label) ->
                    FilterChip(
                        selected = Look.accent == value,
                        onClick = { Look.pickAccent(value) },
                        label = { Text(label) },
                    )
                }
            }
        }
    }
}

// General › Language: keep the full language list, using stock Material list items.
@Composable
fun LanguageCard(core: Core) {
    MacCard(tr("Language")) {
        Column(Modifier.fillMaxWidth()) {
            Text(
                tr("The language TwinDesk uses on this phone"),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 12.dp),
            )
            I18n.LANGUAGES.forEachIndexed { index, (code, label) ->
                if (index > 0) HorizontalDivider(Modifier.padding(start = 16.dp))
                ListItem(
                    headlineContent = { Text(label) },
                    leadingContent = { Text(I18n.FLAGS[code].orEmpty(), style = MaterialTheme.typography.titleMedium) },
                    trailingContent = {
                        RadioButton(
                            selected = I18n.lang == code,
                            onClick = { core.setLanguage(code) },
                        )
                    },
                    modifier = Modifier.clickable { core.setLanguage(code) },
                )
            }
        }
    }
}

@Composable
fun PageHead(title: String, desc: String, master: Boolean? = null, onMaster: (Boolean) -> Unit = {}) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(bottom = 16.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.headlineSmall)
            if (desc.isNotEmpty()) {
                Text(
                    desc,
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 4.dp),
                )
            }
        }
        if (master != null) Switch(checked = master, onCheckedChange = onMaster)
    }
}

@Composable
fun MacSwitch(on: Boolean, onChange: (Boolean) -> Unit, enabled: Boolean = true) =
    Switch(checked = on, onCheckedChange = onChange, enabled = enabled)

// Shared content container backed by the stock Material 3 Card and tonal surface system.
@Composable
fun MacCard(
    title: String = "",
    modifier: Modifier = Modifier,
    action: (@Composable () -> Unit)? = null,
    content: @Composable ColumnScope.() -> Unit,
) {
    Column(modifier.padding(bottom = 20.dp)) {
        if (title.isNotEmpty() || action != null) {
            Row(
                Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, bottom = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (title.isNotEmpty()) {
                    Text(title, style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                } else {
                    Spacer(Modifier.weight(1f))
                }
                action?.invoke()
            }
        }
        Card(
            modifier = Modifier.fillMaxWidth(),
            shape = MaterialTheme.shapes.extraLarge,
            colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow),
            elevation = CardDefaults.cardElevation(defaultElevation = 0.dp),
            content = { Column(content = content) },
        )
    }
}

// Material list item for settings rows. Its built-in two-line layout replaces the custom
// measure policy and automatically handles narrow screens, font scaling and large controls.
@Composable
fun MacRow(
    label: String,
    hint: String = "",
    first: Boolean = false,
    onClick: (() -> Unit)? = null,
    controls: @Composable RowScope.() -> Unit = {},
) {
    if (!first) HorizontalDivider(Modifier.padding(start = 16.dp, end = 16.dp))
    ListItem(
        headlineContent = { Text(label, style = MaterialTheme.typography.bodyLarge) },
        supportingContent = if (hint.isNotEmpty()) {
            { Text(hint, style = MaterialTheme.typography.bodyMedium) }
        } else null,
        trailingContent = {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                content = controls,
            )
        },
        modifier = Modifier.fillMaxWidth().then(if (onClick != null) Modifier.clickable { onClick() } else Modifier),
        colors = ListItemDefaults.colors(containerColor = Color.Transparent),
    )
}

@Composable
fun SwitchRow(label: String, hint: String, on: Boolean, first: Boolean = false, enabled: Boolean = true, onChange: (Boolean) -> Unit) =
    MacRow(label, hint, first) { MacSwitch(on, onChange, enabled) }

// Real displays the computer can mirror. Nothing when there is only one.
@Composable
fun MacDisplayPick(
    displays: List<MacDisplay>,
    selectedId: String,
    onDark: Boolean = false,
    onPick: (String) -> Unit,
) {
    if (displays.size < 2) return
    Row(
        Modifier.horizontalScroll(rememberScrollState()),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        displays.forEach { display ->
            FilterChip(
                selected = display.id == selectedId,
                onClick = { onPick(display.id) },
                label = { Text(display.label(), maxLines = 1, overflow = TextOverflow.Ellipsis) },
            )
        }
    }
}

// Compact contextual message using a tonal Material card rather than a hand-painted box.
@Composable
fun Note(text: String, kind: String = "info") {
    if (text.isEmpty()) return
    val scheme = MaterialTheme.colorScheme
    val container = when (kind) {
        "bad" -> scheme.errorContainer
        "warn" -> scheme.tertiaryContainer
        else -> scheme.primaryContainer
    }
    val content = when (kind) {
        "bad" -> scheme.onErrorContainer
        "warn" -> scheme.onTertiaryContainer
        else -> scheme.onPrimaryContainer
    }
    Card(
        modifier = Modifier.fillMaxWidth().padding(bottom = 14.dp),
        colors = CardDefaults.cardColors(containerColor = container),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp),
    ) {
        Text(
            text,
            color = content,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(16.dp),
        )
    }
}

@Composable
fun Pill(text: String, kind: String = "") {
    val scheme = MaterialTheme.colorScheme
    val (container, content) = when (kind) {
        "good" -> scheme.tertiaryContainer to scheme.onTertiaryContainer
        "bad" -> scheme.errorContainer to scheme.onErrorContainer
        "muted" -> scheme.surfaceContainerHigh to scheme.onSurfaceVariant
        else -> scheme.secondaryContainer to scheme.onSecondaryContainer
    }
    Badge(containerColor = container, contentColor = content) {
        Text(text, style = MaterialTheme.typography.labelMedium)
    }
}

@Composable
fun DirHead(from: String, fromPhone: Boolean, to: String, toPhone: Boolean) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Icon(if (fromPhone) Ico.phone else Ico.display, null, tint = MaterialTheme.colorScheme.primary)
        Text(from, style = MaterialTheme.typography.labelLarge, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
        Icon(Ico.right, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Icon(if (toPhone) Ico.phone else Ico.display, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(to, style = MaterialTheme.typography.labelLarge, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
    }
}

@Composable
fun MacSlider(
    value: Float,
    range: ClosedFloatingPointRange<Float>,
    steps: Int,
    format: (Float) -> String,
    onDone: (Float) -> Unit,
) {
    var current by remember(value) { mutableFloatStateOf(value) }
    Row(verticalAlignment = Alignment.CenterVertically) {
        Slider(
            value = current,
            onValueChange = { current = it },
            valueRange = range,
            steps = steps,
            onValueChangeFinished = { onDone(current) },
            modifier = Modifier.weight(1f),
        )
        Text(
            format(current),
            style = MaterialTheme.typography.labelLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.width(52.dp),
            textAlign = TextAlign.End,
        )
    }
}

// The layout relationship is a single-choice Material control, not a custom diagram widget.
@Composable
fun LayoutPicker(me: String, other: String, side: String, onPick: (String) -> Unit) {
    val options = listOf(
        "left" to tr("Left"),
        "right" to tr("Right"),
        "top" to tr("Above"),
        "bottom" to tr("Below"),
    )
    Column(
        Modifier.fillMaxWidth().padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(tr("Position of {other} relative to {me}", "other" to other, "me" to me), style = MaterialTheme.typography.titleSmall)
        SingleChoiceSegmentedButtonRow(modifier = Modifier.fillMaxWidth()) {
            options.forEachIndexed { index, option ->
                SegmentedButton(
                    selected = side == option.first,
                    onClick = { onPick(option.first) },
                    shape = SegmentedButtonDefaults.itemShape(index = index, count = options.size),
                ) { Text(option.second, maxLines = 1) }
            }
        }
        Text(
            tr("{other} is {where} {me}.", "other" to other, "where" to options.firstOrNull { it.first == side }?.second.orEmpty(), "me" to me),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

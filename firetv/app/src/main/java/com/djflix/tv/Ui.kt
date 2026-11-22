package com.djflix.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil.compose.AsyncImage
import org.json.JSONObject

val Accent = Color(0xFFE50914)
val Bg = Color(0xFF0B0B0D)
val Panel = Color(0xFF17171B)
val Dim = Color(0xFFB3B3B8)
val Faint = Color(0xFF6E6E76)
val EdgeH = 48.dp   // keeps content inside the TV's overscan-safe area
val EdgeV = 27.dp

fun parseColor(hex: String?, fallback: Color = Accent): Color = runCatching { Color(android.graphics.Color.parseColor(hex)) }.getOrDefault(fallback)

fun Modifier.optionalFocus(fr: FocusRequester?): Modifier = if (fr != null) this.focusRequester(fr) else this

/** Remote-friendly button: unmistakable white fill and a slight grow when focused. */
@Composable
fun TvButton(text: String, modifier: Modifier = Modifier, primary: Boolean = false, selected: Boolean = false, focusRequester: FocusRequester? = null, small: Boolean = false, onClick: () -> Unit) {
    var focused by remember { mutableStateOf(false) }
    val bg = when { focused -> Color.White; primary -> Accent; selected -> Color(0x66FFFFFF); else -> Color(0x26FFFFFF) }
    Box(
        modifier.optionalFocus(focusRequester).onFocusChanged { focused = it.isFocused }.scale(if (focused) 1.06f else 1f)
            .clip(RoundedCornerShape(8.dp)).background(bg).clickable(onClick = onClick)
            .padding(horizontal = if (small) 14.dp else 22.dp, vertical = if (small) 7.dp else 10.dp),
        contentAlignment = Alignment.Center,
    ) { Text(text, color = if (focused) Color.Black else Color.White, fontSize = if (small) 13.sp else 15.sp, fontWeight = FontWeight.SemiBold, maxLines = 1) }
}

@Composable
fun Title(text: String, modifier: Modifier = Modifier) = Text(text, modifier = modifier, color = Color.White, fontSize = 28.sp, fontWeight = FontWeight.Bold)

@Composable
fun Body(text: String, modifier: Modifier = Modifier, color: Color = Dim, size: Int = 15, maxLines: Int = Int.MAX_VALUE, align: TextAlign? = null) =
    Text(text, modifier = modifier, color = color, fontSize = size.sp, lineHeight = (size * 1.4).sp, maxLines = maxLines, overflow = TextOverflow.Ellipsis, textAlign = align)

@Composable
fun Loading(modifier: Modifier = Modifier.fillMaxSize()) = Box(modifier, contentAlignment = Alignment.Center) { CircularProgressIndicator(color = Accent) }

@Composable
fun Message(title: String, detail: String, actions: @Composable RowScope.() -> Unit = {}) {
    Column(Modifier.fillMaxSize().padding(EdgeH), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
        Text(title, color = Color.White, fontSize = 24.sp, fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(10.dp))
        Body(detail, Modifier.widthIn(max = 560.dp), align = TextAlign.Center)
        Spacer(Modifier.height(22.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), content = actions)
    }
}

/** Standard "could not load" state with a way out to connection settings. */
@Composable
fun ErrorState(app: AppState, error: String, onRetry: () -> Unit) {
    val fr = remember { FocusRequester() }
    Message("Can't load this right now", error) {
        TvButton("Try again", primary = true, focusRequester = fr, onClick = onRetry)
        TvButton("Connection settings") { app.tab = "settings"; app.reset(Screen.Main) }
    }
    LaunchedEffect(Unit) { runCatching { fr.requestFocus() } }
}

fun cardMeta(c: JSONObject): String = listOfNotNull(
    c.optInt("year").takeIf { it > 0 }?.toString(),
    c.str("certification"),
    if (c.optString("type") == "show") c.optInt("seasonsCount").takeIf { it > 0 }?.let { if (it == 1) "1 Season" else "$it Seasons" } else c.optInt("runtime").takeIf { it > 0 }?.let { "${it / 60}h ${it % 60}m" },
    c.optDouble("rating", 0.0).takeIf { it > 0 }?.let { "★ %.1f".format(it) },
).joinToString("  ·  ")

@Composable
fun PosterCard(app: AppState, c: JSONObject, width: Dp = 112.dp, focusRequester: FocusRequester? = null, onFocus: (() -> Unit)? = null, onClick: () -> Unit) {
    var focused by remember { mutableStateOf(false) }
    Column(Modifier.width(width).optionalFocus(focusRequester).onFocusChanged { focused = it.isFocused; if (it.isFocused) onFocus?.invoke() }.scale(if (focused) 1.08f else 1f).clickable(onClick = onClick)) {
        Box(Modifier.fillMaxWidth().aspectRatio(2f / 3f).clip(RoundedCornerShape(8.dp)).background(Panel).border(if (focused) 3.dp else 0.dp, if (focused) Color.White else Color.Transparent, RoundedCornerShape(8.dp))) {
            val poster = app.cfg.img(c.str("poster"))
            if (poster != null) AsyncImage(model = poster, contentDescription = c.optString("title"), contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
            else Box(Modifier.fillMaxSize().padding(8.dp), contentAlignment = Alignment.Center) { Body(c.optString("title"), color = Color.White, size = 13, align = TextAlign.Center, maxLines = 4) }
            if (c.bool("watched")) Box(Modifier.align(Alignment.TopEnd).padding(5.dp).clip(CircleShape).background(Color(0xCC000000)).padding(horizontal = 6.dp, vertical = 1.dp)) { Text("✓", color = Color.White, fontSize = 11.sp) }
            val pct = c.obj("progress")?.optDouble("percent", 0.0) ?: 0.0
            if (pct > 0 && !c.bool("watched")) Box(Modifier.align(Alignment.BottomStart).fillMaxWidth().height(4.dp).background(Color(0x88000000))) { Box(Modifier.fillMaxHeight().fillMaxWidth((pct / 100).toFloat().coerceIn(0f, 1f)).background(Accent)) }
        }
        Spacer(Modifier.height(6.dp))
        Text(c.optString("title"), color = if (focused) Color.White else Dim, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
fun CardRow(app: AppState, title: String, cards: List<JSONObject>, firstFocus: FocusRequester? = null, onFocus: ((JSONObject) -> Unit)? = null) {
    Column(Modifier.padding(top = 14.dp)) {
        Text(title, Modifier.padding(start = EdgeH, bottom = 8.dp), color = Color.White, fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
        LazyRow(contentPadding = PaddingValues(horizontal = EdgeH, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(14.dp)) {
            items(cards.size, key = { cards[it].optLong("id") }) { i ->
                val c = cards[i]
                PosterCard(app, c, focusRequester = if (i == 0) firstFocus else null, onFocus = { onFocus?.invoke(c) }) { app.go(Screen.Details(c.optLong("id"))) }
            }
        }
    }
}

/** Full-bleed artwork with gradients so text stays readable over any image. */
@Composable
fun Backdrop(url: String?, modifier: Modifier = Modifier) {
    Box(modifier.background(Bg)) {
        if (url != null) AsyncImage(model = url, contentDescription = null, contentScale = ContentScale.Crop, alignment = Alignment.TopEnd, modifier = Modifier.fillMaxSize())
        Box(Modifier.fillMaxSize().background(Brush.horizontalGradient(0f to Bg, 0.45f to Bg.copy(alpha = 0.85f), 1f to Color.Transparent)))
        Box(Modifier.fillMaxSize().background(Brush.verticalGradient(0f to Color.Transparent, 0.6f to Color.Transparent, 1f to Bg)))
    }
}

/**
 * On-screen key grid driven by the remote. Used instead of the system keyboard so focus
 * never gets trapped in a text field and digits/IP addresses are quick to enter.
 */
@Composable
fun Keypad(keys: List<String>, columns: Int, modifier: Modifier = Modifier, keyWidth: Dp = 46.dp, firstFocus: FocusRequester? = null, onKey: (String) -> Unit) {
    Column(modifier, verticalArrangement = Arrangement.spacedBy(6.dp)) {
        keys.chunked(columns).forEachIndexed { r, row ->
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                row.forEachIndexed { i, k ->
                    var focused by remember { mutableStateOf(false) }
                    val wide = k.length > 1
                    Box(
                        Modifier.optionalFocus(if (r == 0 && i == 0) firstFocus else null).onFocusChanged { focused = it.isFocused }
                            .width(if (wide) keyWidth * 2 + 6.dp else keyWidth).height(40.dp).clip(RoundedCornerShape(6.dp))
                            .background(if (focused) Color.White else Color(0x26FFFFFF)).clickable { onKey(k) },
                        contentAlignment = Alignment.Center,
                    ) { Text(k, color = if (focused) Color.Black else Color.White, fontSize = if (wide) 13.sp else 17.sp, fontWeight = FontWeight.SemiBold) }
                }
            }
        }
    }
}

val DigitKeys = listOf("1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "0", "⌫")
val LetterKeys = ("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".map { it.toString() }) + listOf("SPACE", "⌫", "CLEAR")

fun applyKey(current: String, key: String, max: Int = 60): String = when (key) {
    "⌫" -> current.dropLast(1)
    "CLEAR" -> ""
    "SPACE" -> if (current.isEmpty() || current.endsWith(" ")) current else "$current "
    else -> if (current.length < max) current + key else current
}

@Composable
fun FieldBox(label: String, value: String, modifier: Modifier = Modifier, placeholder: String = "") {
    Column(modifier) {
        Text(label, color = Faint, fontSize = 12.sp)
        Spacer(Modifier.height(4.dp))
        Box(Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).background(Panel).border(1.dp, Color(0x33FFFFFF), RoundedCornerShape(8.dp)).padding(horizontal = 14.dp, vertical = 10.dp)) {
            Text(if (value.isEmpty()) placeholder else value, color = if (value.isEmpty()) Faint else Color.White, fontSize = 20.sp, fontWeight = FontWeight.Medium, maxLines = 1)
        }
    }
}

@Composable
fun Chips(options: List<Pair<String, String>>, selected: String, modifier: Modifier = Modifier, onSelect: (String) -> Unit) {
    LazyRow(modifier, contentPadding = PaddingValues(horizontal = EdgeH, vertical = 4.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        items(options) { (id, label) -> TvButton(label, small = true, selected = id == selected) { onSelect(id) } }
    }
}

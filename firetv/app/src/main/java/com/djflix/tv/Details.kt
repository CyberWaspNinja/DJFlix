package com.djflix.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import coil.compose.AsyncImage
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject

fun fmtTime(ms: Long): String { val s = ms / 1000; val h = s / 3600; val m = (s % 3600) / 60; val sec = s % 60; return if (h > 0) "%d:%02d:%02d".format(h, m, sec) else "%d:%02d".format(m, sec) }

@Composable
fun DetailsScreen(app: AppState, id: Long) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    val data = rememberLoader(app, id) { app.api.obj("/media/$id?profileId=${app.pid}") }
    val play = remember { FocusRequester() }
    var season by remember(id) { mutableStateOf(-1) }
    var focusedOnce by remember(id) { mutableStateOf(false) }
    val d = data.value
    if (d == null) { if (data.error != null) ErrorState(app, data.error, data.retry) else Loading(); return }

    val c = d.obj("card") ?: JSONObject()
    val isShow = c.optString("type") == "show"
    val pid = app.pid
    val available = d.optString("availability") == "available"
    val upNext = d.obj("upNext")
    val seasons = d.arr("seasons")
    if (season == -1 && isShow) season = upNext?.optInt("season") ?: seasons.firstOrNull()?.optInt("season") ?: 1
    val episodes = if (isShow) rememberLoader(app, id, season) { app.api.list("/shows/$id/episodes?season=$season&profileId=$pid") } else null
    LaunchedEffect(id, d) { if (!focusedOnce) { delay(150); runCatching { play.requestFocus() }; focusedOnce = true } }

    fun act(block: suspend () -> Unit) { scope.launch { runCatching { block(); app.dataVersion++ }.onFailure { app.handle(it) } } }
    val prog = c.obj("progress")
    val inProgress = (prog?.long("positionMs") ?: 0) > 0 && prog?.bool("isCompleted") != true
    val resumeLabel = when {
        isShow && upNext != null -> (if ((upNext.obj("progress")?.long("positionMs") ?: 0) > 0) "▶  Resume" else "▶  Play") + "  S${upNext.optInt("season")} E${upNext.optInt("episode")}"
        inProgress -> "▶  Resume from ${fmtTime(prog!!.long("positionMs"))}"
        else -> "▶  Play"
    }

    Box(Modifier.fillMaxSize()) {
        Backdrop(app.cfg.img(d.str("backdrop")), Modifier.fillMaxWidth().height(380.dp))
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 36.dp, bottom = 40.dp)) {
            item(key = "head") {
                Column(Modifier.padding(horizontal = EdgeH).width(600.dp)) {
                    Text(c.optString("title"), color = Color.White, fontSize = 36.sp, fontWeight = FontWeight.Black, lineHeight = 40.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    Spacer(Modifier.height(6.dp))
                    Body(cardMeta(c), color = Color.White, size = 14)
                    Body(c.strings("genres").joinToString(" · "), size = 13)
                    Spacer(Modifier.height(8.dp))
                    Body(c.str("overview") ?: "No description is available for this title.", maxLines = 4, size = 14)
                    Spacer(Modifier.height(8.dp))
                    val cast = d.arr("cast").take(5).joinToString(", ") { it.optString("name") }
                    if (cast.isNotEmpty()) Body("Cast: $cast", size = 13, maxLines = 1)
                    val directors = d.strings("directors").joinToString(", ")
                    if (directors.isNotEmpty()) Body((if (isShow) "Created by: " else "Director: ") + directors, size = 13, maxLines = 1)
                    val studios = d.strings("studios").take(3).joinToString(", ")
                    if (studios.isNotEmpty()) Body("Studio: $studios", size = 13, maxLines = 1)
                    if (!available) { Spacer(Modifier.height(6.dp)); Body(if (d.optString("availability") == "missing") "The file for this title is currently missing from the PC." else "This title is not currently playable.", color = Color(0xFFFFB4A9), size = 13) }
                }
                Spacer(Modifier.height(14.dp))
                LazyRow(contentPadding = PaddingValues(horizontal = EdgeH, vertical = 6.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    if (available) item { TvButton(resumeLabel, primary = true, focusRequester = play) { app.go(Screen.Player(id, if (isShow) upNext?.optLong("episodeId") else null)) } }
                    if (available && !isShow && inProgress) item { TvButton("Play from beginning") { app.go(Screen.Player(id, null, startOver = true)) } }
                    d.obj("trailer")?.let { t -> item { TvButton("Trailer", focusRequester = if (available) null else play) { openTrailer(ctx, app, t) } } }
                    item { TvButton(if (c.bool("inWatchlist")) "✓  My List" else "+  My List") { act { if (c.bool("inWatchlist")) app.api.delete("/profiles/$pid/watchlist/$id") else app.api.put("/profiles/$pid/watchlist/$id") } } }
                    item { TvButton(if (c.bool("watched")) "Mark unwatched" else "Mark watched") { act { app.api.post("/profiles/$pid/watched", json("mediaId" to id, "watched" to !c.bool("watched"))) } } }
                    if (inProgress || (isShow && prog != null)) item { TvButton("Reset progress") { act { app.api.delete("/profiles/$pid/progress/$id") } } }
                    item {
                        val r = if (c.isNull("userRating")) 0 else c.optInt("userRating")
                        TvButton("Rate: " + "★".repeat(r) + "☆".repeat(5 - r)) { act { app.api.put("/profiles/$pid/ratings/$id", json("rating" to if (r >= 5) 0 else r + 1)) } }
                    }
                    d.strings("directors").firstOrNull()?.let { n -> item { TvButton("More from $n") { app.go(Screen.Person(n)) } } }
                    d.arr("cast").firstOrNull()?.optString("name")?.takeIf { it.isNotEmpty() }?.let { n -> item { TvButton("More with $n") { app.go(Screen.Person(n)) } } }
                }
            }

            if (isShow) {
                item(key = "seasons") {
                    Spacer(Modifier.height(10.dp))
                    Chips(seasons.map { it.optInt("season").toString() to ((it.str("name") ?: "Season ${it.optInt("season")}") + "  (${it.optInt("availableCount")}/${it.optInt("episodeCount")})") }, season.toString()) { season = it.toInt() }
                }
                val eps = episodes?.value
                if (eps == null) item(key = "eploading") { Loading(Modifier.fillMaxWidth().height(120.dp)) }
                else items(eps.size, key = { "ep" + eps[it].optLong("id") }) { i -> EpisodeRow(app, eps[i], id) }
            }

            val similar = d.arr("similar")
            if (similar.isNotEmpty()) item(key = "similar") { CardRow(app, "More Like This", similar) }
        }
    }
}

@Composable
private fun EpisodeRow(app: AppState, e: JSONObject, showId: Long) {
    var focused by remember { mutableStateOf(false) }
    val available = e.bool("available")
    val p = e.obj("progress")
    Row(
        Modifier.padding(horizontal = EdgeH, vertical = 4.dp).fillMaxWidth().onFocusChanged { focused = it.isFocused }.clip(RoundedCornerShape(10.dp))
            .background(if (focused) Color(0x33FFFFFF) else Color.Transparent).border(if (focused) 2.dp else 0.dp, if (focused) Color.White else Color.Transparent, RoundedCornerShape(10.dp))
            .clickable { if (available) app.go(Screen.Player(showId, e.optLong("id"))) else app.say("This episode is not in your library.") }.padding(8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.width(150.dp).aspectRatio(16f / 9f).clip(RoundedCornerShape(6.dp)).background(Panel)) {
            app.cfg.img(e.str("still"))?.let { AsyncImage(model = it, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize()) }
            val pct = p?.optDouble("percent", 0.0) ?: 0.0
            if (pct > 0 && !e.bool("watched")) Box(Modifier.align(Alignment.BottomStart).fillMaxWidth().height(4.dp).background(Color(0x88000000))) { Box(Modifier.fillMaxHeight().fillMaxWidth((pct / 100).toFloat().coerceIn(0f, 1f)).background(Accent)) }
        }
        Spacer(Modifier.width(14.dp))
        Column(Modifier.weight(1f)) {
            Text("${e.optInt("episode")}. ${e.str("title") ?: "Episode ${e.optInt("episode")}"}" + if (e.bool("watched")) "   ✓" else "", color = if (available) Color.White else Faint, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Body(listOfNotNull(e.optInt("runtime").takeIf { it > 0 }?.let { "$it min" }, e.str("airDate"), if (!available) "Not in library" else null).joinToString("  ·  "), size = 12, color = Faint)
            Body(e.str("overview") ?: "", size = 13, maxLines = 2)
        }
    }
}

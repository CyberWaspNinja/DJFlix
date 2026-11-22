package com.djflix.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject

/** Loads data for a screen and exposes loading / error / value with a retry trigger. */
class Loader<T>(val value: T?, val error: String?, val loading: Boolean, val retry: () -> Unit)

@Composable
fun <T> rememberLoader(app: AppState, vararg keys: Any?, block: suspend () -> T): Loader<T> {
    var value by remember { mutableStateOf<T?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var loading by remember { mutableStateOf(true) }
    var attempt by remember { mutableStateOf(0) }
    LaunchedEffect(attempt, app.dataVersion, *keys) {
        loading = value == null; error = null
        try { value = block() }
        catch (e: ApiException) { if (e.unpaired) app.handle(e) else error = e.message }
        loading = false
    }
    return Loader(value, error, loading) { attempt++ }
}

// ---- profiles -------------------------------------------------------------------------------
@Composable
fun ProfilesScreen(app: AppState) {
    val scope = rememberCoroutineScope()
    val data = rememberLoader(app) { app.api.list("/profiles") }
    var pinFor by remember { mutableStateOf<JSONObject?>(null) }
    var creating by remember { mutableStateOf(false) }
    val focus = remember { FocusRequester() }

    when {
        pinFor != null -> {
            val p = pinFor!!
            var pin by remember { mutableStateOf("") }
            var err by remember { mutableStateOf<String?>(null) }
            LaunchedEffect(Unit) { delay(100); runCatching { focus.requestFocus() } }
            Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
                Title("Enter the PIN for ${p.optString("name")}")
                Spacer(Modifier.height(16.dp))
                Text("●".repeat(pin.length).padEnd(4, '○'), color = Color.White, fontSize = 30.sp, letterSpacing = 8.sp)
                Spacer(Modifier.height(8.dp))
                Body(err ?: " ", color = Color(0xFFFFB4A9))
                Spacer(Modifier.height(8.dp))
                Keypad(listOf("1", "2", "3", "4", "5", "6", "7", "8", "9", "OK", "0", "⌫"), 3, keyWidth = 62.dp, firstFocus = focus) { k ->
                    if (k != "OK") { pin = applyKey(pin, k, 8); err = null }
                    else scope.launch {
                        runCatching { app.api.post("/profiles/${p.long("id")}/verify-pin", json("pin" to pin)) }
                            .onSuccess { if (it.bool("ok")) app.selectProfile(p) else { err = "That PIN is not correct."; pin = "" } }
                            .onFailure { err = it.message; pin = "" }
                    }
                }
                Spacer(Modifier.height(14.dp))
                TvButton("Cancel", small = true) { pinFor = null }
            }
        }
        creating -> {
            var name by remember { mutableStateOf("") }
            LaunchedEffect(Unit) { delay(100); runCatching { focus.requestFocus() } }
            Column(Modifier.fillMaxSize().padding(horizontal = EdgeH, vertical = EdgeV), horizontalAlignment = Alignment.CenterHorizontally) {
                Title("New profile")
                Spacer(Modifier.height(14.dp))
                FieldBox("Name", name, Modifier.width(420.dp), placeholder = "Type a name")
                Spacer(Modifier.height(16.dp))
                Keypad(LetterKeys, 9, firstFocus = focus) { k -> name = applyKey(name, if (k.length == 1 && name.isNotEmpty() && !name.endsWith(" ")) k.lowercase() else k, 20) }
                Spacer(Modifier.height(16.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    TvButton("Create", primary = true) {
                        if (name.isBlank()) app.say("Type a name first.")
                        else scope.launch {
                            val colors = listOf("#E50914", "#2E86DE", "#27AE60", "#F39C12", "#8E44AD", "#16A085")
                            runCatching { app.api.post("/profiles", json("name" to name.trim(), "color" to colors.random())) }.onSuccess { app.selectProfile(it) }.onFailure { app.handle(it) }
                        }
                    }
                    TvButton("Cancel") { creating = false }
                }
                Spacer(Modifier.height(10.dp))
                Body("PINs, avatars and viewing restrictions are set in DJFlix on the PC.", size = 13)
            }
        }
        data.error != null -> ErrorStateSetup(app, data.error, data.retry)
        data.value == null -> Loading()
        else -> {
            val profiles = data.value
            LaunchedEffect(profiles.size) { delay(100); runCatching { focus.requestFocus() } }
            Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
                Text("DJFLIX", color = Accent, fontSize = 30.sp, fontWeight = FontWeight.Black)
                Spacer(Modifier.height(10.dp))
                Title("Who's watching?")
                Spacer(Modifier.height(26.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(26.dp)) {
                    val last = app.cfg.profileId
                    val focusIndex = profiles.indexOfFirst { it.long("id") == last }.coerceAtLeast(0)
                    profiles.take(6).forEachIndexed { i, p ->
                        Avatar(p.optString("name"), parseColor(p.str("color")), p.bool("hasPin"), if (i == focusIndex) focus else null) { if (p.bool("hasPin")) pinFor = p else app.selectProfile(p) }
                    }
                    if (profiles.size < 6) Avatar("Add profile", Color(0xFF2A2A30), false, if (profiles.isEmpty()) focus else null, symbol = "+") { creating = true }
                }
                Spacer(Modifier.height(30.dp))
                TvButton("Connection settings", small = true) { app.go(Screen.Setup("test")) }
            }
        }
    }
}

@Composable
private fun ErrorStateSetup(app: AppState, error: String, retry: () -> Unit) {
    val fr = remember { FocusRequester() }
    Message("Can't reach DJFlix", "$error\n\nServer: ${app.cfg.host}:${app.cfg.port}") {
        TvButton("Retry", primary = true, focusRequester = fr, onClick = retry)
        TvButton("Change server") { app.go(Screen.Setup("manual")) }
        TvButton("Run setup again") { app.go(Screen.Setup("network")) }
    }
    LaunchedEffect(Unit) { delay(100); runCatching { fr.requestFocus() } }
}

@Composable
private fun Avatar(name: String, color: Color, locked: Boolean, focusRequester: FocusRequester?, symbol: String? = null, onClick: () -> Unit) {
    var focused by remember { mutableStateOf(false) }
    Column(Modifier.optionalFocus(focusRequester).onFocusChanged { focused = it.isFocused }.scale(if (focused) 1.1f else 1f).clickable(onClick = onClick), horizontalAlignment = Alignment.CenterHorizontally) {
        Box(Modifier.size(96.dp).clip(RoundedCornerShape(12.dp)).background(color).border(if (focused) 4.dp else 0.dp, if (focused) Color.White else Color.Transparent, RoundedCornerShape(12.dp)), contentAlignment = Alignment.Center) {
            Text(symbol ?: name.take(1).uppercase(), color = Color.White, fontSize = 40.sp, fontWeight = FontWeight.Bold)
        }
        Spacer(Modifier.height(8.dp))
        Text(name + if (locked) "  🔒" else "", color = if (focused) Color.White else Dim, fontSize = 14.sp)
    }
}

// ---- main shell with top navigation ---------------------------------------------------------
private val Tabs = listOf("search" to "Search", "home" to "Home", "movies" to "Movies", "shows" to "TV Shows", "mylist" to "My List", "settings" to "Settings")

@Composable
fun MainScreen(app: AppState) {
    val navFocus = remember { FocusRequester() }
    if (app.profile == null) {
        // Relaunch with a remembered profile: reload it rather than asking again (PIN profiles still ask).
        val data = rememberLoader(app) { app.api.list("/profiles") }
        LaunchedEffect(data.value) {
            val p = data.value?.firstOrNull { it.long("id") == app.cfg.profileId }
            if (data.value != null) { if (p != null && !p.bool("hasPin")) app.profile = p else app.reset(Screen.Profiles) }
        }
        if (data.error != null) ErrorStateSetup(app, data.error, data.retry) else Loading()
        return
    }
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(start = EdgeH, end = EdgeH, top = 18.dp, bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("DJFLIX", color = Accent, fontSize = 22.sp, fontWeight = FontWeight.Black)
            Spacer(Modifier.width(22.dp))
            Tabs.forEach { (id, label) ->
                TvButton(label, Modifier.padding(end = 6.dp), small = true, selected = app.tab == id, focusRequester = if (id == app.tab) navFocus else null) { app.tab = id }
            }
            Spacer(Modifier.weight(1f))
            TvButton(app.profile?.optString("name") ?: "Profile", small = true) { app.reset(Screen.Profiles) }
        }
        Box(Modifier.fillMaxSize()) {
            when (app.tab) {
                "home" -> HomeTab(app, navFocus)
                "movies" -> GridTab(app, "movie")
                "shows" -> GridTab(app, "show")
                "mylist" -> MyListTab(app)
                "search" -> SearchTab(app)
                else -> SettingsTab(app)
            }
        }
    }
}

// ---- home -----------------------------------------------------------------------------------
@Composable
fun HomeTab(app: AppState, navFocus: FocusRequester) {
    val scope = rememberCoroutineScope()
    val data = rememberLoader(app, app.pid) { app.api.obj("/home?profileId=${app.pid}") }
    val play = remember { FocusRequester() }
    val home = data.value
    when {
        data.error != null && home == null -> ErrorState(app, data.error, data.retry)
        home == null -> Loading()
        else -> {
            val hero = home.obj("hero"); val rows = home.arr("rows")
            LaunchedEffect(hero?.obj("card")?.optLong("id")) { delay(150); runCatching { if (hero != null) play.requestFocus() else navFocus.requestFocus() } }
            if (hero == null && rows.isEmpty()) {
                Message("Your library is empty", "Add a media folder or connect a debrid account in DJFlix on the PC. Titles appear here as soon as the scan finishes.") { TvButton("Refresh", primary = true) { app.dataVersion++ } }
                return
            }
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 40.dp)) {
                if (hero != null) item(key = "hero") {
                    val c = hero.obj("card") ?: JSONObject()
                    Box(Modifier.fillMaxWidth().height(330.dp)) {
                        Backdrop(app.cfg.img(hero.str("backdrop")), Modifier.fillMaxSize())
                        Column(Modifier.align(Alignment.BottomStart).padding(start = EdgeH, bottom = 18.dp).width(520.dp)) {
                            Text(c.optString("title"), color = Color.White, fontSize = 38.sp, fontWeight = FontWeight.Black, maxLines = 2, lineHeight = 42.sp)
                            Spacer(Modifier.height(6.dp))
                            Body(cardMeta(c) + c.strings("genres").take(3).joinToString(", ").let { if (it.isEmpty()) "" else "  ·  $it" }, color = Color.White, size = 14)
                            Spacer(Modifier.height(8.dp))
                            Body(hero.str("overview") ?: "", maxLines = 3, size = 14)
                            Spacer(Modifier.height(14.dp))
                            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                                val inProgress = (c.obj("progress")?.long("positionMs") ?: 0) > 0 && !c.bool("watched")
                                TvButton(if (inProgress) "▶  Resume" else "▶  Play", primary = true, focusRequester = play) { app.go(Screen.Player(c.optLong("id"))) }
                                TvButton("More Info") { app.go(Screen.Details(c.optLong("id"))) }
                                var listed by remember(c.optLong("id")) { mutableStateOf(c.bool("inWatchlist")) }
                                TvButton(if (listed) "✓  My List" else "+  My List") {
                                    scope.launch { runCatching { if (listed) app.api.delete("/profiles/${app.pid}/watchlist/${c.optLong("id")}") else app.api.put("/profiles/${app.pid}/watchlist/${c.optLong("id")}"); listed = !listed; app.dataVersion++ }.onFailure { app.handle(it) } }
                                }
                                hero.obj("trailer")?.let { t -> val ctx = androidx.compose.ui.platform.LocalContext.current; TvButton("Trailer") { openTrailer(ctx, app, t) } }
                            }
                        }
                    }
                }
                items(rows.size, key = { rows[it].optString("id") }) { i -> CardRow(app, rows[i].optString("title"), rows[i].arr("items")) }
            }
        }
    }
}

fun openTrailer(ctx: android.content.Context, app: AppState, trailer: JSONObject) {
    val url = trailer.str("url") ?: return
    val intent = android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse(url)).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
    try { ctx.startActivity(intent) } catch (e: Exception) { app.say("Install the YouTube app on this Fire TV to watch trailers.") }
}

// ---- movies / shows grid --------------------------------------------------------------------
@Composable
fun CardGrid(app: AppState, cards: List<JSONObject>, modifier: Modifier = Modifier, columns: Int = 7, onEnd: (() -> Unit)? = null, header: (@Composable () -> Unit)? = null) {
    LazyVerticalGrid(GridCells.Fixed(columns), modifier, contentPadding = PaddingValues(start = EdgeH, end = EdgeH, top = 10.dp, bottom = 40.dp), horizontalArrangement = Arrangement.spacedBy(14.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
        if (header != null) item(span = { GridItemSpan(maxLineSpan) }) { header() }
        items(cards.size, key = { cards[it].optLong("id") }) { i ->
            if (i >= cards.size - columns) LaunchedEffect(cards.size) { onEnd?.invoke() }
            PosterCard(app, cards[i], width = 200.dp) { app.go(Screen.Details(cards[i].optLong("id"))) }
        }
    }
}

@Composable
fun GridTab(app: AppState, type: String) {
    var genre by remember(type) { mutableStateOf("") }
    var sort by remember(type) { mutableStateOf("popularity") }
    var filter by remember(type) { mutableStateOf("all") }
    var cards by remember(type) { mutableStateOf(listOf<JSONObject>()) }
    var total by remember(type) { mutableStateOf(-1) }
    var page by remember(type) { mutableStateOf(1) }
    var error by remember(type) { mutableStateOf<String?>(null) }
    val genres = rememberLoader(app, type) { app.api.list("/genres?type=$type&profileId=${app.pid}") }
    val path = if (type == "movie") "/movies" else "/shows"

    LaunchedEffect(type, genre, sort, filter, page, app.dataVersion) {
        try {
            val q = buildString {
                append("$path?profileId=${app.pid}&pageSize=60&page=$page&sort=$sort")
                if (genre.isNotEmpty()) append("&genre=${enc(genre)}")
                when (filter) { "unwatched" -> append("&watched=false"); "watched" -> append("&watched=true"); "mylist" -> append("&watchlist=true") }
            }
            val res = app.api.obj(q)
            total = res.optInt("total"); error = null
            cards = if (page == 1) res.arr("items") else (cards + res.arr("items")).distinctBy { it.optLong("id") }
        } catch (e: ApiException) { if (e.unpaired) app.handle(e) else error = e.message }
    }
    fun resetTo(block: () -> Unit) { block(); page = 1 }

    Column(Modifier.fillMaxSize()) {
        Chips(listOf("" to "All genres") + (genres.value ?: emptyList()).map { it.optString("name") to it.optString("name") }, genre) { resetTo { genre = it } }
        Row {
            Chips(listOf("popularity" to "Popular", "added" to "Recently added", "rating" to "Top rated", "year" to "Newest", "title" to "A–Z"), sort, Modifier.weight(1.4f)) { resetTo { sort = it } }
            Chips(listOf("all" to "All", "unwatched" to "Unwatched", "watched" to "Watched", "mylist" to "My List"), filter, Modifier.weight(1f)) { resetTo { filter = it } }
        }
        when {
            error != null && cards.isEmpty() -> ErrorState(app, error!!) { app.dataVersion++ }
            total < 0 -> Loading()
            cards.isEmpty() -> Message(if (type == "movie") "No movies match" else "No TV shows match", "Try a different genre or filter.")
            else -> CardGrid(app, cards, onEnd = { if (cards.size < total) page = cards.size / 60 + 1 })
        }
    }
}

// ---- my list --------------------------------------------------------------------------------
@Composable
fun MyListTab(app: AppState) {
    var type by remember { mutableStateOf("all") }
    val data = rememberLoader(app, type) { app.api.list("/profiles/${app.pid}/watchlist?type=$type") }
    Column(Modifier.fillMaxSize()) {
        Chips(listOf("all" to "All", "movie" to "Movies", "show" to "TV Shows"), type) { type = it }
        val cards = data.value
        when {
            data.error != null && cards == null -> ErrorState(app, data.error, data.retry)
            cards == null -> Loading()
            cards.isEmpty() -> Message("Your list is empty", "Add movies and shows with the \"+ My List\" button and they will appear here on every device.")
            else -> CardGrid(app, cards)
        }
    }
}

// ---- search ---------------------------------------------------------------------------------
@Composable
fun SearchTab(app: AppState) {
    val scope = rememberCoroutineScope()
    var q by remember { mutableStateOf("") }
    var type by remember { mutableStateOf("all") }
    var result by remember { mutableStateOf<JSONObject?>(null) }
    var recent by remember { mutableStateOf(listOf<String>()) }
    var recentVersion by remember { mutableStateOf(0) }
    val keys = remember { FocusRequester() }
    LaunchedEffect(Unit) { delay(120); runCatching { keys.requestFocus() } }
    LaunchedEffect(recentVersion) { runCatching { recent = (app.api.call("GET", "/profiles/${app.pid}/search-history") as? JSONArray)?.let { a -> (0 until a.length()).map { a.optString(it) } } ?: emptyList() } }
    // Debounced so results follow typing without a request per key press.
    LaunchedEffect(q, type) {
        if (q.isBlank()) { result = null; return@LaunchedEffect }
        delay(300)
        runCatching { app.api.obj("/search?q=${enc(q.trim())}&type=$type&profileId=${app.pid}") }.onSuccess { result = it }.onFailure { app.handle(it) }
        delay(2500)   // remember a query once the user has paused on it
        runCatching { app.api.post("/profiles/${app.pid}/search-history", json("query" to q.trim().lowercase())); recentVersion++ }
    }
    Row(Modifier.fillMaxSize()) {
        Column(Modifier.width(360.dp).fillMaxHeight().verticalScroll(rememberScrollState()).padding(start = EdgeH, top = 6.dp, end = 12.dp)) {
            FieldBox("Search movies, shows, actors, directors, genres", q, placeholder = "Start typing")
            Spacer(Modifier.height(10.dp))
            Keypad(LetterKeys, 6, keyWidth = 44.dp, firstFocus = keys) { k -> q = applyKey(q, k, 40) }
            Spacer(Modifier.height(10.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                listOf("all" to "All", "movie" to "Movies", "show" to "TV").forEach { (id, label) -> TvButton(label, small = true, selected = type == id) { type = id } }
            }
            if (recent.isNotEmpty()) {
                Spacer(Modifier.height(14.dp))
                Text("Recent searches", color = Faint, fontSize = 12.sp)
                Spacer(Modifier.height(6.dp))
                recent.take(6).forEach { r -> TvButton(r, Modifier.padding(bottom = 6.dp), small = true) { q = r.uppercase() } }
                TvButton("Clear history", small = true) { scope.launch { runCatching { app.api.delete("/profiles/${app.pid}/search-history") }; recentVersion++ } }
            }
            Spacer(Modifier.height(30.dp))
        }
        val res = result
        when {
            q.isBlank() -> Message("Find something to watch", "Use the keys on the left. Results update as you type.")
            res == null -> Loading()
            res.arr("results").isEmpty() -> Message("No results for \"${q.trim().lowercase()}\"", "Check the spelling, or try an actor, a director or a genre.")
            else -> CardGrid(app, res.arr("results"), columns = 5, header = {
                val people = res.arr("people")
                if (people.isNotEmpty()) Row(Modifier.padding(bottom = 4.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    people.take(4).forEach { p -> TvButton("${p.optString("name")} · ${p.optString("role")}", small = true) { app.go(Screen.Person(p.optString("name"))) } }
                }
            })
        }
    }
}

@Composable
fun PersonScreen(app: AppState, name: String) {
    val data = rememberLoader(app, name) { app.api.obj("/people/${enc(name)}?profileId=${app.pid}") }
    val v = data.value
    when {
        data.error != null && v == null -> ErrorState(app, data.error, data.retry)
        v == null -> Loading()
        else -> {
            val cards = (v.arr("asDirector") + v.arr("asActor")).distinctBy { it.optLong("id") }
            Column(Modifier.fillMaxSize().padding(top = EdgeV)) {
                Title(name, Modifier.padding(start = EdgeH))
                Body("More from $name in your library", Modifier.padding(start = EdgeH, bottom = 8.dp))
                if (cards.isEmpty()) Message("Nothing else yet", "No other titles with $name are in your library.") else CardGrid(app, cards)
            }
        }
    }
}

// ---- settings -------------------------------------------------------------------------------
@Composable
fun SettingsTab(app: AppState) {
    val scope = rememberCoroutineScope()
    var status by remember { mutableStateOf("Not tested") }
    val prefs = app.prefs
    fun set(key: String, value: Any) { scope.launch { runCatching { app.savePrefs(json(key to value)) }.onFailure { app.handle(it) } } }
    fun <T> cycle(options: List<T>, current: T): T = options[(options.indexOf(current) + 1).mod(options.size)]

    val speeds = listOf(0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 3.0, 4.0)
    val langs = listOf("off", "en", "es", "fr", "de", "it", "pt", "ar", "hi", "ml")
    val speed = prefs.optDouble("playbackSpeed", 1.0)
    val seek = prefs.optInt("seekSeconds", 10)
    val subs = prefs.optString("subtitleLanguage", "off")
    val audio = prefs.optString("audioLanguage", "en")
    val subSize = prefs.optString("subtitleSize", "medium")

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = EdgeH).padding(bottom = 40.dp)) {
        Section("Playback")
        SettingRow("Autoplay next episode", if (prefs.optBoolean("autoplayNext", true)) "On" else "Off") { set("autoplayNext", !prefs.optBoolean("autoplayNext", true)) }
        SettingRow("Default playback speed", "${speed}x") { set("playbackSpeed", cycle(speeds, speed)) }
        SettingRow("Skip interval", "$seek seconds") { set("seekSeconds", cycle(listOf(10, 15, 30, 60), seek)) }
        SettingRow("Subtitles", if (subs == "off") "Off" else subs.uppercase()) { set("subtitleLanguage", cycle(langs, subs)) }
        SettingRow("Subtitle size", subSize.replaceFirstChar { it.uppercase() }) { set("subtitleSize", cycle(listOf("small", "medium", "large"), subSize)) }
        SettingRow("Audio language", audio.uppercase()) { set("audioLanguage", cycle(langs.drop(1), audio)) }
        SettingRow("When a title has progress", if (prefs.optString("resumeBehaviour", "resume") == "resume") "Resume automatically" else "Start from the beginning") { set("resumeBehaviour", if (prefs.optString("resumeBehaviour", "resume") == "resume") "restart" else "resume") }

        Section("Profile")
        SettingRow("Current profile", app.profile?.optString("name") ?: "") { app.reset(Screen.Profiles) }

        Section("Connection")
        SettingRow("Server", "${app.cfg.serverName.ifEmpty { "DJFlix" }}  ·  ${app.cfg.host}:${app.cfg.port}  ·  v${app.cfg.serverVersion}") { }
        SettingRow("Test connection", status) {
            status = "Testing…"
            scope.launch { status = runCatching { app.api.obj("/me") }.fold({ "Connected and paired" }, { (it as? ApiException)?.let { e -> if (e.unpaired) "Not paired any more" else if (e.unreachable) "PC not reachable" else e.message } ?: "Failed" }) }
        }
        SettingRow("Reconnect", "Reload everything from the PC") { app.dataVersion++; app.tab = "home" }
        SettingRow("Change server", "Enter a different PC address") { app.go(Screen.Setup("manual")) }
        SettingRow("Run setup wizard again", "Network check, discovery and pairing") { app.go(Screen.Setup("network")) }
        SettingRow("Forget this server", "Remove the pairing from this Fire TV") { app.cfg.forgetServer(); app.profile = null; app.reset(Screen.Setup("network")) }

        Section("About")
        Body("DJFlix for Fire TV 1.0.0. Metadata and artwork are supplied by TMDB. This product uses the TMDB API but is not endorsed or certified by TMDB.", size = 13)
    }
}

@Composable
private fun Section(title: String) = Text(title, Modifier.padding(top = 18.dp, bottom = 8.dp), color = Color.White, fontSize = 18.sp, fontWeight = FontWeight.Bold)

@Composable
private fun SettingRow(label: String, value: String, onClick: () -> Unit) {
    var focused by remember { mutableStateOf(false) }
    Row(
        Modifier.fillMaxWidth().padding(bottom = 6.dp).onFocusChanged { focused = it.isFocused }.clip(RoundedCornerShape(8.dp))
            .background(if (focused) Color.White else Panel).clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 11.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, color = if (focused) Color.Black else Color.White, fontSize = 15.sp, fontWeight = FontWeight.Medium, modifier = Modifier.weight(1f))
        Text(value, color = if (focused) Color(0xFF333333) else Dim, fontSize = 14.sp)
    }
}

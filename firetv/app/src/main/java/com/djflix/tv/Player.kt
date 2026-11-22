@file:androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)

package com.djflix.tv

import android.view.KeyEvent
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.focusable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.key.onKeyEvent
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.TrackSelectionOverride
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.media3.ui.CaptionStyleCompat
import androidx.media3.ui.PlayerView
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject

private val Speeds = listOf(0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 3f, 4f)
private data class TrackOption(val label: String, val groupIndex: Int, val trackIndex: Int, val selected: Boolean)

private fun tracksOf(player: Player, type: Int): List<TrackOption> {
    val out = mutableListOf<TrackOption>()
    player.currentTracks.groups.forEachIndexed { gi, g ->
        if (g.type != type) return@forEachIndexed
        for (ti in 0 until g.length) {
            if (!g.isTrackSupported(ti)) continue
            val f = g.getTrackFormat(ti)
            val label = listOfNotNull(f.label, f.language?.takeIf { it != "und" }?.uppercase(), if (type == C.TRACK_TYPE_AUDIO && f.channelCount > 0) "${f.channelCount}ch" else null).distinct().joinToString(" · ").ifEmpty { "Track ${out.size + 1}" }
            out += TrackOption(label, gi, ti, g.isTrackSelected(ti))
        }
    }
    return out
}

private fun selectTrack(player: Player, type: Int, opt: TrackOption?) {
    val b = player.trackSelectionParameters.buildUpon().clearOverridesOfType(type)
    if (opt == null) b.setTrackTypeDisabled(type, true)
    else { b.setTrackTypeDisabled(type, false); b.setOverrideForType(TrackSelectionOverride(player.currentTracks.groups[opt.groupIndex].mediaTrackGroup, opt.trackIndex)) }
    player.trackSelectionParameters = b.build()
}

/**
 * Remote-driven player. With the menu closed: OK = play/pause, ◀ ▶ = skip, ▲ = show info,
 * ▼ or Menu = options. Progress is reported to the hub every ten seconds and on pause, stop and end.
 */
@Composable
fun PlayerScreen(app: AppState, first: Screen.Player) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var req by remember { mutableStateOf(first) }
    var session by remember { mutableStateOf<JSONObject?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var buffering by remember { mutableStateOf(true) }
    var playing by remember { mutableStateOf(false) }
    var pos by remember { mutableStateOf(0L) }
    var dur by remember { mutableStateOf(0L) }
    var showInfo by remember { mutableStateOf(true) }
    var infoAt by remember { mutableStateOf(0L) }
    var panel by remember { mutableStateOf<String?>(null) }   // null | menu | speed | subs | audio
    var osd by remember { mutableStateOf<String?>(null) }
    var speed by remember { mutableStateOf(app.prefs.optDouble("playbackSpeed", 1.0).toFloat()) }
    var resize by remember { mutableStateOf(AspectRatioFrameLayout.RESIZE_MODE_FIT) }
    var countdown by remember { mutableStateOf(-1) }
    var refreshed by remember { mutableStateOf(false) }
    var trackVersion by remember { mutableStateOf(0) }
    var otherDevice by remember { mutableStateOf<String?>(null) }
    var attempt by remember { mutableStateOf(0) }
    val keyFocus = remember { FocusRequester() }
    val panelFocus = remember { FocusRequester() }
    val seekMs = app.prefs.optInt("seekSeconds", 10) * 1000L

    val player = remember {
        val http = DefaultHttpDataSource.Factory().setAllowCrossProtocolRedirects(true).setConnectTimeoutMs(15000).setReadTimeoutMs(30000)
        ExoPlayer.Builder(ctx).setMediaSourceFactory(DefaultMediaSourceFactory(http)).build()
    }

    fun flash(msg: String) { osd = msg; scope.launch { delay(1400); if (osd == msg) osd = null } }
    fun poke() { showInfo = true; infoAt = System.currentTimeMillis() }

    /** Reports progress; if the hub cannot be reached the update is kept and sent later with its age. */
    fun report(event: String) {
        val s = session ?: return
        val d = player.duration.takeIf { it > 0 } ?: dur
        val p = player.currentPosition.coerceAtLeast(0)
        if (d <= 0 && event != "ended") return
        val body = json("mediaId" to s.long("mediaId"), "episodeId" to s.obj("episode")?.long("episodeId"), "positionMs" to p, "durationMs" to d, "sessionId" to s.optString("sessionId"), "event" to event)
        val pid = app.pid
        app.scope.launch {
            runCatching { app.api.put("/profiles/$pid/progress", body) }
                .onSuccess { r -> app.cfg.pendingProgress = ""; otherDevice = r.obj("otherSession")?.str("deviceName") }
                .onFailure { e -> if (e is ApiException && e.unreachable) app.cfg.pendingProgress = json("pid" to pid, "at" to System.currentTimeMillis(), "body" to body).toString() }
        }
    }

    fun exit() { report("stop"); app.dataVersion++; app.back() }

    fun load(s: JSONObject, startAt: Long, refresh: Boolean = false) {
        val url = app.cfg.base + s.optString("streamUrl") + if (refresh) "?refresh=1" else ""
        val subs = s.arr("subtitles").map { sub ->
            val mime = when (sub.optString("format")) { "vtt" -> MimeTypes.TEXT_VTT; "ass", "ssa" -> MimeTypes.TEXT_SSA; else -> MimeTypes.APPLICATION_SUBRIP }
            MediaItem.SubtitleConfiguration.Builder(android.net.Uri.parse(app.cfg.base + sub.optString("url"))).setMimeType(mime).setLanguage(sub.str("language")).setLabel(sub.str("label")).build()
        }
        player.setMediaItem(MediaItem.Builder().setUri(url).setSubtitleConfigurations(subs).build(), startAt)
        player.prepare(); player.playWhenReady = true
    }

    // Start (or restart) whenever the requested title or episode changes.
    LaunchedEffect(req, attempt) {
        session = null; error = null; buffering = true; countdown = -1; refreshed = false; pos = 0; dur = 0
        app.flushPendingProgress()
        val body = json("profileId" to app.pid, "mediaId" to req.mediaId, "episodeId" to req.episodeId, "startOver" to (req.startOver || app.prefs.optString("resumeBehaviour", "resume") == "restart"))
        try {
            val s = app.api.post("/playback/sessions", body)
            session = s
            val prefs = app.prefs
            val subLang = prefs.optString("subtitleLanguage", "off")
            val params = player.trackSelectionParameters.buildUpon().setPreferredAudioLanguage(prefs.optString("audioLanguage", "en"))
            if (subLang == "off") params.setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true) else params.setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false).setPreferredTextLanguage(subLang)
            player.trackSelectionParameters = params.clearOverridesOfType(C.TRACK_TYPE_TEXT).clearOverridesOfType(C.TRACK_TYPE_AUDIO).build()
            player.setPlaybackSpeed(speed)
            load(s, s.long("resumePositionMs"))
            poke()
        } catch (e: ApiException) {
            if (e.unpaired) app.handle(e) else error = e.message
        }
    }

    DisposableEffect(Unit) {
        val listener = object : Player.Listener {
            override fun onPlaybackStateChanged(state: Int) {
                buffering = state == Player.STATE_BUFFERING
                if (state == Player.STATE_ENDED) {
                    report("ended")
                    val next = session?.obj("next")
                    if (next == null) { app.dataVersion++; app.back() }
                    else if (app.prefs.optBoolean("autoplayNext", true)) countdown = 10
                    else { panel = "next" }
                }
            }
            override fun onIsPlayingChanged(isPlaying: Boolean) { playing = isPlaying; if (!isPlaying && player.playbackState == Player.STATE_READY) report("pause") }
            override fun onTracksChanged(tracks: androidx.media3.common.Tracks) { trackVersion++ }
            override fun onPlayerError(e: PlaybackException) {
                val s = session
                // Provider links expire: ask the hub for a fresh one once before giving up.
                if (s != null && s.optString("kind") == "debrid" && !refreshed) { refreshed = true; load(s, player.currentPosition.coerceAtLeast(0), refresh = true); return }
                report("pause")
                error = when (e.errorCode) {
                    PlaybackException.ERROR_CODE_DECODER_INIT_FAILED, PlaybackException.ERROR_CODE_DECODING_FORMAT_UNSUPPORTED, PlaybackException.ERROR_CODE_PARSING_CONTAINER_UNSUPPORTED ->
                        "This Fire TV cannot play this file's format (${s?.optString("container")?.uppercase()}). Try another version of the title if one is available."
                    PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED, PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT -> "The connection to the PC was lost. Check the network and try again; playback will resume where it stopped."
                    else -> "Playback stopped unexpectedly (${e.errorCodeName})."
                }
            }
        }
        player.addListener(listener)
        onDispose { player.removeListener(listener); player.release() }
    }

    // Position ticker, periodic progress reports and auto-hide of the info bar.
    LaunchedEffect(Unit) {
        var lastReport = 0L
        while (true) {
            delay(500)
            pos = player.currentPosition.coerceAtLeast(0); dur = player.duration.coerceAtLeast(0)
            val now = System.currentTimeMillis()
            if (player.isPlaying && now - lastReport >= 10_000) { lastReport = now; report("tick") }
            if (showInfo && player.isPlaying && panel == null && now - infoAt > 4500) showInfo = false
        }
    }
    LaunchedEffect(countdown) {
        if (countdown > 0) { delay(1000); if (countdown > 0) countdown-- }
        else if (countdown == 0) { countdown = -1; session?.obj("next")?.let { req = Screen.Player(req.mediaId, it.long("episodeId")) } }
    }
    LaunchedEffect(panel, error, countdown) { delay(80); runCatching { if (panel != null || error != null || countdown >= 0) panelFocus.requestFocus() else keyFocus.requestFocus() } }

    BackHandler { when { countdown >= 0 -> { countdown = -1; exit() }; panel != null && panel != "menu" && panel != "next" -> panel = "menu"; panel != null -> panel = null; else -> exit() } }

    fun seekBy(delta: Long) { val target = (player.currentPosition + delta).coerceIn(0, if (player.duration > 0) player.duration else Long.MAX_VALUE); player.seekTo(target); pos = target; poke(); flash(if (delta > 0) "+${delta / 1000}s" else "${delta / 1000}s") }
    fun toggle() { if (player.isPlaying) { player.pause(); flash("Paused") } else { player.play(); flash("Play") }; poke() }
    fun goEpisode(target: JSONObject?) { if (target != null) { report("stop"); panel = null; req = Screen.Player(req.mediaId, target.long("episodeId")) } }

    val markers = session?.obj("markers")
    val introEnd = markers?.optLong("introEndMs", 0) ?: 0
    val inIntro = markers != null && introEnd > 0 && pos >= markers.optLong("introStartMs", 0) && pos < introEnd

    Box(
        // onKeyEvent sits before the focus target so it receives the keys of this node and of the panels inside it.
        Modifier.fillMaxSize().background(Color.Black).onKeyEvent { ev ->
            if (ev.nativeKeyEvent.action != KeyEvent.ACTION_DOWN || panel != null || error != null || countdown >= 0) return@onKeyEvent false
            when (ev.nativeKeyEvent.keyCode) {
                KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER, KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE -> { toggle(); true }
                KeyEvent.KEYCODE_MEDIA_PLAY -> { player.play(); poke(); true }
                KeyEvent.KEYCODE_MEDIA_PAUSE -> { player.pause(); poke(); true }
                KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_MEDIA_REWIND -> { seekBy(-seekMs); true }
                KeyEvent.KEYCODE_DPAD_RIGHT, KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> { seekBy(seekMs); true }
                KeyEvent.KEYCODE_DPAD_UP -> { if (inIntro) { player.seekTo(introEnd); flash("Intro skipped") } else poke(); true }
                KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_MENU -> { panel = "menu"; poke(); true }
                KeyEvent.KEYCODE_MEDIA_NEXT -> { goEpisode(session?.obj("next")); true }
                KeyEvent.KEYCODE_MEDIA_PREVIOUS -> { goEpisode(session?.obj("previous")); true }
                else -> false
            }
        }.focusRequester(keyFocus).focusable(),
    ) {
        AndroidView(
            factory = { c -> val v = PlayerView(c); v.useController = false; v.keepScreenOn = true; v.isFocusable = false; v.player = player; v },
            update = { v ->
                v.resizeMode = resize
                val scale = when (app.prefs.optString("subtitleSize", "medium")) { "small" -> 0.8f; "large" -> 1.5f; else -> 1.1f }
                v.subtitleView?.setFractionalTextSize(0.0533f * scale)
                v.subtitleView?.setStyle(CaptionStyleCompat(android.graphics.Color.WHITE, android.graphics.Color.TRANSPARENT, android.graphics.Color.TRANSPARENT, CaptionStyleCompat.EDGE_TYPE_OUTLINE, android.graphics.Color.BLACK, null))
            },
            modifier = Modifier.fillMaxSize(),
        )

        val s = session
        if (buffering && error == null) Loading()

        // ---- info bar -------------------------------------------------------------------
        if (s != null && (showInfo || !playing) && error == null && countdown < 0) {
            Box(Modifier.fillMaxWidth().align(Alignment.TopStart).background(Brush.verticalGradient(listOf(Color(0xCC000000), Color.Transparent))).padding(horizontal = EdgeH, vertical = EdgeV)) {
                Column {
                    Text(s.optString("title"), color = Color.White, fontSize = 22.sp, fontWeight = FontWeight.Bold)
                    s.obj("episode")?.let { e -> Body("S${e.optInt("season")} E${e.optInt("episode")}  ·  ${e.str("title") ?: ""}", color = Color.White, size = 14) }
                    otherDevice?.let { Body("Also playing on $it. The most recent position wins.", color = Color(0xFFFFD27F), size = 12) }
                }
            }
            Column(Modifier.fillMaxWidth().align(Alignment.BottomStart).background(Brush.verticalGradient(listOf(Color.Transparent, Color(0xE6000000)))).padding(horizontal = EdgeH).padding(top = 40.dp, bottom = EdgeV)) {
                Box(Modifier.fillMaxWidth().height(6.dp).clip(RoundedCornerShape(3.dp)).background(Color(0x55FFFFFF))) {
                    Box(Modifier.fillMaxHeight().fillMaxWidth(if (dur > 0) (pos.toFloat() / dur).coerceIn(0f, 1f) else 0f).background(Accent))
                }
                Spacer(Modifier.height(8.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text((if (playing) "▶  " else "❚❚  ") + fmtTime(pos) + "  /  " + fmtTime(dur), color = Color.White, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                    Spacer(Modifier.weight(1f))
                    Body(listOfNotNull(if (speed != 1f) "${speed}x" else null, s.str("quality"), "OK play/pause  ·  ◀ ▶ skip ${seekMs / 1000}s  ·  ▼ options").joinToString("   ·   "), size = 12)
                }
            }
        }

        // ---- skip intro / credits ---------------------------------------------------------
        if (inIntro && panel == null && error == null) Box(Modifier.align(Alignment.BottomEnd).padding(end = EdgeH, bottom = 110.dp).clip(RoundedCornerShape(8.dp)).background(Accent).padding(horizontal = 18.dp, vertical = 10.dp)) { Text("▲  Skip intro", color = Color.White, fontSize = 15.sp, fontWeight = FontWeight.SemiBold) }

        osd?.let { Box(Modifier.align(Alignment.Center).clip(RoundedCornerShape(12.dp)).background(Color(0xB3000000)).padding(horizontal = 26.dp, vertical = 14.dp)) { Text(it, color = Color.White, fontSize = 24.sp, fontWeight = FontWeight.Bold) } }

        // ---- next episode countdown -------------------------------------------------------
        val next = s?.obj("next")
        if (countdown >= 0 && next != null) {
            Column(Modifier.align(Alignment.BottomEnd).padding(end = EdgeH, bottom = EdgeV).clip(RoundedCornerShape(12.dp)).background(Color(0xF217171B)).padding(18.dp).width(330.dp)) {
                Body("Next episode in $countdown", color = Color.White, size = 13)
                Text("S${next.optInt("season")} E${next.optInt("episode")}  ·  ${next.str("title") ?: ""}", color = Color.White, fontSize = 17.sp, fontWeight = FontWeight.Bold, maxLines = 2)
                Spacer(Modifier.height(12.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    TvButton("Play now", primary = true, focusRequester = panelFocus) { countdown = 0 }
                    TvButton("Cancel") { countdown = -1; app.dataVersion++; app.back() }
                }
            }
        }

        // ---- options panel ----------------------------------------------------------------
        if (panel != null && error == null && s != null) {
            Column(Modifier.align(Alignment.CenterEnd).fillMaxHeight().width(340.dp).background(Color(0xF20E0E11)).padding(horizontal = 22.dp, vertical = EdgeV).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                @Composable fun Item(label: String, first: Boolean = false, selected: Boolean = false, onClick: () -> Unit) = TvButton(label, Modifier.fillMaxWidth(), selected = selected, focusRequester = if (first) panelFocus else null, onClick = onClick)
                val textTracks = remember(trackVersion, panel) { tracksOf(player, C.TRACK_TYPE_TEXT) }
                val audioTracks = remember(trackVersion, panel) { tracksOf(player, C.TRACK_TYPE_AUDIO) }
                when (panel) {
                    "menu" -> {
                        Text("Options", color = Color.White, fontSize = 20.sp, fontWeight = FontWeight.Bold)
                        Item(if (playing) "Pause" else "Play", first = true) { toggle(); panel = null }
                        Item("Speed: ${speed}x") { panel = "speed" }
                        Item("Subtitles: " + (textTracks.firstOrNull { it.selected }?.label ?: if (textTracks.isEmpty()) "none available" else "Off")) { if (textTracks.isEmpty()) flash("No subtitles in this file") else panel = "subs" }
                        Item("Audio: " + (audioTracks.firstOrNull { it.selected }?.label ?: "Default")) { if (audioTracks.size < 2) flash("Only one audio track") else panel = "audio" }
                        Item("Picture: " + when (resize) { AspectRatioFrameLayout.RESIZE_MODE_FIT -> "Fit"; AspectRatioFrameLayout.RESIZE_MODE_ZOOM -> "Zoom"; else -> "Stretch" }) {
                            resize = when (resize) { AspectRatioFrameLayout.RESIZE_MODE_FIT -> AspectRatioFrameLayout.RESIZE_MODE_ZOOM; AspectRatioFrameLayout.RESIZE_MODE_ZOOM -> AspectRatioFrameLayout.RESIZE_MODE_FILL; else -> AspectRatioFrameLayout.RESIZE_MODE_FIT }
                        }
                        Item("Restart from beginning") { player.seekTo(0); player.play(); panel = null; flash("Restarted") }
                        s.obj("next")?.let { n -> Item("Next: S${n.optInt("season")} E${n.optInt("episode")}") { goEpisode(n) } }
                        s.obj("previous")?.let { n -> Item("Previous: S${n.optInt("season")} E${n.optInt("episode")}") { goEpisode(n) } }
                        Item("Stop and exit") { exit() }
                    }
                    "speed" -> {
                        Text("Playback speed", color = Color.White, fontSize = 20.sp, fontWeight = FontWeight.Bold)
                        Speeds.forEach { v -> Item("${v}x", first = v == speed, selected = v == speed) { speed = v; player.setPlaybackSpeed(v); panel = "menu"; flash("${v}x") } }
                    }
                    "subs" -> {
                        Text("Subtitles", color = Color.White, fontSize = 20.sp, fontWeight = FontWeight.Bold)
                        Item("Off", first = true, selected = textTracks.none { it.selected }) { selectTrack(player, C.TRACK_TYPE_TEXT, null); panel = "menu" }
                        textTracks.forEach { t -> Item(t.label, selected = t.selected) { selectTrack(player, C.TRACK_TYPE_TEXT, t); panel = "menu" } }
                    }
                    "audio" -> {
                        Text("Audio", color = Color.White, fontSize = 20.sp, fontWeight = FontWeight.Bold)
                        audioTracks.forEachIndexed { i, t -> Item(t.label, first = i == 0, selected = t.selected) { selectTrack(player, C.TRACK_TYPE_AUDIO, t); panel = "menu" } }
                    }
                    "next" -> {
                        Text("Finished", color = Color.White, fontSize = 20.sp, fontWeight = FontWeight.Bold)
                        s.obj("next")?.let { n -> Item("Play next: S${n.optInt("season")} E${n.optInt("episode")}", first = true) { goEpisode(n) } }
                        Item("Exit") { app.dataVersion++; app.back() }
                    }
                }
            }
        }

        // ---- errors -----------------------------------------------------------------------
        error?.let { msg ->
            Box(Modifier.fillMaxSize().background(Color(0xE6000000))) {
                Message("Can't play this title", msg) {
                    TvButton("Try again", primary = true, focusRequester = panelFocus) {
                        val keep = pos; val cur = session
                        error = null; refreshed = false
                        if (cur != null) load(cur, keep, refresh = cur.optString("kind") == "debrid") else attempt++
                    }
                    TvButton("Back") { app.dataVersion++; app.back() }
                }
            }
        }
    }
}

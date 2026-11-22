package com.djflix.tv

import androidx.compose.foundation.background
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject

private data class Check(val label: String, val ok: Boolean?, val detail: String)

/**
 * First-run wizard: network → find the PC → (manual address) → test → pair → done.
 * It can be re-entered at any step from Settings.
 */
@Composable
fun SetupWizard(app: AppState, startAt: String) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var step by remember { mutableStateOf(startAt) }
    var net by remember { mutableStateOf(detectNetwork(ctx)) }
    var host by remember { mutableStateOf(app.cfg.host) }
    var port by remember { mutableStateOf(app.cfg.port.toString()) }
    var health by remember { mutableStateOf<JSONObject?>(null) }
    val focus = remember { FocusRequester() }
    LaunchedEffect(step) { delay(120); runCatching { focus.requestFocus() } }
    val base = "http://$host:${port.toIntOrNull() ?: DEFAULT_PORT}"
    val steps = listOf("network" to "Network", "discover" to "Find PC", "manual" to "Address", "test" to "Test", "pair" to "Pair")

    Column(Modifier.fillMaxSize().padding(horizontal = EdgeH, vertical = EdgeV)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("DJFLIX", color = Accent, fontSize = 26.sp, fontWeight = FontWeight.Black)
            Spacer(Modifier.width(24.dp))
            steps.forEach { (id, label) ->
                val active = id == step
                Box(Modifier.padding(end = 8.dp).clip(RoundedCornerShape(20.dp)).background(if (active) Color.White else Color(0x1FFFFFFF)).padding(horizontal = 12.dp, vertical = 4.dp)) {
                    Text(label, color = if (active) Color.Black else Dim, fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
                }
            }
        }
        Spacer(Modifier.height(22.dp))

        when (step) {
            // ---- 1. network ---------------------------------------------------------------
            "network" -> Column {
                Title("Check your network")
                Spacer(Modifier.height(8.dp))
                Body("This Fire TV and the PC running DJFlix should normally be on the same home network.")
                Spacer(Modifier.height(18.dp))
                if (net.connected) {
                    InfoLine("Connection", net.type)
                    InfoLine("This device", net.ip ?: "unknown")
                    InfoLine("Subnet", net.subnet ?: "unknown")
                    InfoLine("Router (gateway)", net.gateway ?: "not reported")
                } else Body("No network connection was found. Connect this Fire TV to Wi-Fi or Ethernet in the Fire TV settings, then check again.", color = Color(0xFFFFB4A9))
                Spacer(Modifier.height(24.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    TvButton("Find my PC", primary = true, focusRequester = focus) { step = "discover" }
                    TvButton("Check again") { net = detectNetwork(ctx) }
                    TvButton("Enter address manually") { step = "manual" }
                }
            }

            // ---- 2. discovery -------------------------------------------------------------
            "discover" -> {
                var searching by remember { mutableStateOf(true) }
                var found by remember { mutableStateOf(listOf<FoundServer>()) }
                var round by remember { mutableStateOf(0) }
                LaunchedEffect(round) { searching = true; found = discoverServers(net); searching = false; delay(100); runCatching { focus.requestFocus() } }
                Column {
                    Title("Looking for DJFlix on your network")
                    Spacer(Modifier.height(8.dp))
                    when {
                        searching -> { Body("Searching…"); Spacer(Modifier.height(20.dp)); Loading(Modifier.size(60.dp)) }
                        found.isEmpty() -> Body("No DJFlix PC answered. Make sure DJFlix is running on the PC, that both devices are on the same network, and that Windows Firewall allows DJFlix. You can also type the PC's address yourself.")
                        else -> {
                            Body("Choose the PC to connect to.")
                            Spacer(Modifier.height(14.dp))
                            found.forEachIndexed { i, s ->
                                TvButton("${s.name}   ·   ${s.host}:${s.port}", Modifier.padding(bottom = 10.dp), primary = true, focusRequester = if (i == 0) focus else null) { host = s.host; port = s.port.toString(); step = "test" }
                            }
                        }
                    }
                    Spacer(Modifier.height(20.dp))
                    if (!searching) Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        TvButton("Search again", focusRequester = if (found.isEmpty()) focus else null) { round++ }
                        TvButton("Enter address manually") { step = "manual" }
                        TvButton("Back") { step = "network" }
                    }
                }
            }

            // ---- 3. manual address --------------------------------------------------------
            "manual" -> {
                var field by remember { mutableStateOf("host") }
                Row {
                    Column(Modifier.weight(1f).padding(end = 40.dp)) {
                        Title("Enter the IP address of the computer running DJFlix")
                        Spacer(Modifier.height(8.dp))
                        Body("You'll find it on the DJFlix dashboard on the PC, shown as the server address.")
                        Spacer(Modifier.height(18.dp))
                        FieldBox(if (field == "host") "PC IP address  ◀ typing here" else "PC IP address", host, placeholder = "192.168.1.50")
                        Spacer(Modifier.height(12.dp))
                        FieldBox(if (field == "port") "Port  ◀ typing here" else "Port", port, Modifier.width(220.dp), placeholder = DEFAULT_PORT.toString())
                        Spacer(Modifier.height(14.dp))
                        Body("Your router (gateway) is ${net.gateway ?: "unknown"}. That is usually NOT the PC's address; it is shown only to help you recognise your network's number range.", size = 13)
                    }
                    Column {
                        Keypad(DigitKeys, 3, keyWidth = 62.dp, firstFocus = focus) { k ->
                            if (field == "host") host = applyKey(host, k, 15) else if (k != ".") port = applyKey(port, k, 5)
                        }
                        Spacer(Modifier.height(14.dp))
                        TvButton(if (field == "host") "Edit port" else "Edit IP address", small = true) { field = if (field == "host") "port" else "host" }
                        Spacer(Modifier.height(8.dp))
                        TvButton("Test connection", primary = true) {
                            val okHost = Regex("""^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$""").matchEntire(host)?.groupValues?.drop(1)?.all { it.toInt() <= 255 } == true
                            val p = port.toIntOrNull()
                            if (!okHost) app.say("Enter a full IP address such as 192.168.1.50.")
                            else if (p == null || p !in 1024..65535) app.say("The port must be between 1024 and 65535.")
                            else step = "test"
                        }
                        Spacer(Modifier.height(8.dp))
                        TvButton("Search automatically", small = true) { step = "discover" }
                    }
                }
            }

            // ---- 4. test ------------------------------------------------------------------
            "test" -> {
                var checks by remember { mutableStateOf(listOf<Check>()) }
                var running by remember { mutableStateOf(true) }
                var round by remember { mutableStateOf(0) }
                var canPair by remember { mutableStateOf(false) }
                var alreadyPaired by remember { mutableStateOf(false) }
                LaunchedEffect(round) {
                    running = true; canPair = false; alreadyPaired = false
                    val out = mutableListOf<Check>()
                    out += Check("Network connection", net.connected, if (net.connected) "${net.type}, ${net.ip}" else "This Fire TV has no network connection.")
                    checks = out.toList()
                    val h = runCatching { app.api.call("GET", "/health", base = base, auth = false) as JSONObject }
                    val err = h.exceptionOrNull() as? ApiException
                    out += Check("DJFlix reachable at $host:$port", h.isSuccess, when {
                        h.isSuccess -> "Connected to \"${h.getOrNull()?.optString("name")}\"."
                        err?.unreachable == true -> "No answer. Check that the PC is on, DJFlix is running, the address is the PC's (not the router's), and Windows Firewall allows DJFlix on this port."
                        else -> err?.message ?: "Unexpected reply."
                    })
                    checks = out.toList()
                    if (h.isSuccess) {
                        val o = h.getOrThrow(); health = o
                        val compatible = o.optInt("apiVersion") >= CLIENT_API_VERSION && o.optInt("minClientApiVersion", 1) <= CLIENT_API_VERSION
                        out += Check("Version compatibility", compatible, if (compatible) "Server ${o.optString("version")}." else "This app and DJFlix on the PC are different versions. Update both to the latest release.")
                        val svc = o.obj("services")
                        out += Check("Required services", svc?.bool("database") == true, buildString {
                            append("Database ready.")
                            append(if (svc?.bool("metadata") == true) " Metadata connected." else " Metadata is not configured on the PC yet (titles will use file names).")
                            append(if (svc?.bool("debrid") == true) " Debrid connected." else " No debrid account connected (local library only).")
                        })
                        if (compatible) {
                            // An existing token is only reused if it belongs to this very server.
                            val sameServer = app.cfg.token.isNotEmpty() && app.cfg.serverId == o.optString("serverId")
                            val me = if (sameServer) runCatching { app.api.call("GET", "/me", base = base) } else null
                            alreadyPaired = me?.isSuccess == true
                            out += Check("Pairing and authentication", if (alreadyPaired) true else null, if (alreadyPaired) "This device is already paired." else "Not paired yet. Continue to pair this device.")
                            canPair = true
                        }
                    }
                    checks = out.toList(); running = false
                    delay(100); runCatching { focus.requestFocus() }
                }
                Column(Modifier.verticalScroll(rememberScrollState())) {
                    Title("Testing the connection")
                    Spacer(Modifier.height(14.dp))
                    checks.forEach { c ->
                        Row(Modifier.padding(bottom = 10.dp)) {
                            Text(when (c.ok) { true -> "✓"; false -> "✕"; else -> "•" }, color = when (c.ok) { true -> Color(0xFF58D68D); false -> Accent; else -> Dim }, fontSize = 20.sp, fontWeight = FontWeight.Bold, modifier = Modifier.width(34.dp))
                            Column { Text(c.label, color = Color.White, fontSize = 16.sp, fontWeight = FontWeight.SemiBold); Body(c.detail, size = 13) }
                        }
                    }
                    if (running) Body("Working…")
                    Spacer(Modifier.height(16.dp))
                    if (!running) Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        if (canPair) TvButton(if (alreadyPaired) "Finish" else "Continue to pairing", primary = true, focusRequester = focus) {
                            if (alreadyPaired) { saveServer(app, host, port, health, null); app.reset(Screen.Profiles) } else step = "pair"
                        }
                        TvButton("Test again", focusRequester = if (canPair) null else focus) { round++ }
                        TvButton("Change address") { step = "manual" }
                    }
                }
            }

            // ---- 5. pairing ---------------------------------------------------------------
            "pair" -> {
                var code by remember { mutableStateOf("") }
                var busy by remember { mutableStateOf(false) }
                var waiting by remember { mutableStateOf<JSONObject?>(null) }
                var error by remember { mutableStateOf<String?>(null) }

                fun finish(res: JSONObject) { saveServer(app, host, port, health, res); app.say("Paired with ${app.cfg.serverName}."); app.reset(Screen.Profiles) }

                // While waiting for approval on the PC, poll until it is approved, denied or expires.
                LaunchedEffect(waiting) {
                    val w = waiting ?: return@LaunchedEffect
                    while (true) {
                        delay(2000)
                        val r = runCatching { app.api.call("GET", "/pair/status/${w.optString("requestId")}?secret=${enc(w.optString("secret"))}", base = base, auth = false) as JSONObject }
                        val o = r.getOrNull()
                        if (o == null) { if ((r.exceptionOrNull() as? ApiException)?.status == 404) { waiting = null; error = "The request expired. Try again."; break }; continue }
                        when (o.optString("status")) {
                            "paired" -> { finish(o); break }
                            "denied" -> { waiting = null; error = "Pairing was declined on the PC."; break }
                            "expired" -> { waiting = null; error = "The request expired. Try again."; break }
                        }
                    }
                }

                Row {
                    Column(Modifier.weight(1f).padding(end = 40.dp)) {
                        Title("Pair this Fire TV")
                        Spacer(Modifier.height(8.dp))
                        if (waiting != null) {
                            Body("Open DJFlix on the PC and approve \"${deviceName()}\" under Devices.")
                            Spacer(Modifier.height(16.dp)); Loading(Modifier.size(60.dp)); Spacer(Modifier.height(16.dp))
                            TvButton("Cancel", focusRequester = focus) { waiting = null }
                        } else {
                            Body("On the PC, open DJFlix → Devices → Pair a device, and type the 6-digit code shown there. Codes last ten minutes and work once.")
                            Spacer(Modifier.height(18.dp))
                            FieldBox("Pairing code", code.chunked(3).joinToString(" "), Modifier.width(260.dp), placeholder = "••• •••")
                            error?.let { Spacer(Modifier.height(10.dp)); Body(it, color = Color(0xFFFFB4A9)) }
                            Spacer(Modifier.height(18.dp))
                            Body("No keyboard on the PC handy? Choose \"Ask the PC to approve\" and click Approve in DJFlix instead.", size = 13)
                        }
                    }
                    if (waiting == null) Column {
                        Keypad(DigitKeys.filter { it != "." }.let { it.take(9) + listOf("0", "⌫") }, 3, keyWidth = 62.dp, firstFocus = focus) { k -> code = applyKey(code, k, 6); error = null }
                        Spacer(Modifier.height(14.dp))
                        TvButton(if (busy) "Pairing…" else "Pair", primary = true) {
                            if (busy) return@TvButton
                            if (code.length != 6) { error = "Enter all six digits."; return@TvButton }
                            busy = true
                            scope.launch {
                                runCatching { app.api.call("POST", "/pair/request", json("deviceName" to deviceName(), "deviceKind" to "firetv", "code" to code), base = base, auth = false) as JSONObject }
                                    .onSuccess { finish(it) }.onFailure { error = it.message; code = "" }
                                busy = false
                            }
                        }
                        Spacer(Modifier.height(8.dp))
                        TvButton("Ask the PC to approve", small = true) {
                            scope.launch {
                                runCatching { app.api.call("POST", "/pair/request", json("deviceName" to deviceName(), "deviceKind" to "firetv"), base = base, auth = false) as JSONObject }
                                    .onSuccess { error = null; waiting = it }.onFailure { error = it.message }
                            }
                        }
                        Spacer(Modifier.height(8.dp))
                        TvButton("Back", small = true) { step = "test" }
                    }
                }
            }
        }
    }
}

@Composable
private fun InfoLine(label: String, value: String) {
    Row(Modifier.padding(bottom = 8.dp)) {
        Text(label, color = Faint, fontSize = 15.sp, modifier = Modifier.width(190.dp))
        Text(value, color = Color.White, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
    }
}

/** Persists the server and, after pairing, this device's identity. Nothing here is a provider credential. */
private fun saveServer(app: AppState, host: String, port: String, health: JSONObject?, paired: JSONObject?) {
    val c = app.cfg
    c.host = host; c.port = port.toIntOrNull() ?: DEFAULT_PORT
    health?.let { c.serverId = it.optString("serverId"); c.serverName = it.optString("name"); c.serverVersion = it.optString("version") }
    paired?.let {
        c.token = it.optString("token"); c.deviceId = it.optString("deviceId")
        it.str("serverId")?.let { v -> c.serverId = v }; it.str("serverName")?.let { v -> c.serverName = v }; it.str("version")?.let { v -> c.serverVersion = v }
        c.profileId = 0
    }
}

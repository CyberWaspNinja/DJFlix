package com.djflix.tv

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.Uri
import android.os.Build
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import org.json.JSONTokener
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.util.concurrent.TimeUnit

const val CLIENT_API_VERSION = 1
const val DEFAULT_PORT = 8674
const val DISCOVERY_PORT = 47810

// ---- JSON helpers ---------------------------------------------------------------------------
fun JSONObject.str(k: String): String? = if (isNull(k)) null else optString(k).takeIf { it.isNotEmpty() }
fun JSONObject.obj(k: String): JSONObject? = optJSONObject(k)
fun JSONObject.arr(k: String): List<JSONObject> = optJSONArray(k).objs()
fun JSONObject.strings(k: String): List<String> = optJSONArray(k)?.let { a -> (0 until a.length()).map { a.optString(it) } } ?: emptyList()
fun JSONObject.bool(k: String): Boolean = optBoolean(k, false)
fun JSONObject.long(k: String): Long = optLong(k, 0)
fun JSONArray?.objs(): List<JSONObject> = if (this == null) emptyList() else (0 until length()).mapNotNull { optJSONObject(it) }
fun json(vararg pairs: Pair<String, Any?>): JSONObject = JSONObject().apply { pairs.forEach { (k, v) -> put(k, v ?: JSONObject.NULL) } }

// ---- persisted configuration ----------------------------------------------------------------
/** Everything the app remembers between launches: the paired server, this device's identity and the chosen profile. */
class Config(ctx: Context) {
    private val p = ctx.getSharedPreferences("djflix", Context.MODE_PRIVATE)
    var host: String get() = p.getString("host", "")!!; set(v) = p.edit().putString("host", v).apply()
    var port: Int get() = p.getInt("port", DEFAULT_PORT); set(v) = p.edit().putInt("port", v).apply()
    var token: String get() = p.getString("token", "")!!; set(v) = p.edit().putString("token", v).apply()
    var deviceId: String get() = p.getString("deviceId", "")!!; set(v) = p.edit().putString("deviceId", v).apply()
    var serverId: String get() = p.getString("serverId", "")!!; set(v) = p.edit().putString("serverId", v).apply()
    var serverName: String get() = p.getString("serverName", "")!!; set(v) = p.edit().putString("serverName", v).apply()
    var serverVersion: String get() = p.getString("serverVersion", "")!!; set(v) = p.edit().putString("serverVersion", v).apply()
    var profileId: Long get() = p.getLong("profileId", 0); set(v) = p.edit().putLong("profileId", v).apply()
    var pendingProgress: String get() = p.getString("pendingProgress", "")!!; set(v) = p.edit().putString("pendingProgress", v).apply()

    val base: String get() = "http://$host:$port"
    val paired: Boolean get() = host.isNotEmpty() && token.isNotEmpty()
    fun forgetServer() { p.edit().remove("host").remove("port").remove("token").remove("deviceId").remove("serverId").remove("serverName").remove("serverVersion").remove("profileId").remove("pendingProgress").apply() }
    /** Absolute URL for artwork paths returned by the hub. */
    fun img(path: String?): String? = when { path.isNullOrEmpty() -> null; path.startsWith("http") -> path; else -> base + path }
}

// ---- API client -----------------------------------------------------------------------------
class ApiException(val status: Int, val code: String, override val message: String) : Exception(message) {
    val unreachable get() = status == 0
    val unpaired get() = status == 401
}

class Api(private val cfg: Config) {
    private val client = OkHttpClient.Builder().connectTimeout(6, TimeUnit.SECONDS).readTimeout(25, TimeUnit.SECONDS).build()
    private val jsonType = "application/json".toMediaType()

    suspend fun call(method: String, path: String, body: JSONObject? = null, base: String? = null, auth: Boolean = true): Any? = withContext(Dispatchers.IO) {
        val rb = Request.Builder().url((base ?: cfg.base) + "/api/v1" + path)
        if (auth && cfg.token.isNotEmpty()) rb.header("Authorization", "Bearer " + cfg.token)
        val payload = body?.toString() ?: if (method == "GET" || method == "DELETE") null else "{}"
        rb.method(method, payload?.toRequestBody(jsonType))
        try {
            client.newCall(rb.build()).execute().use { res ->
                val text = res.body?.string().orEmpty()
                if (!res.isSuccessful) {
                    val o = runCatching { JSONObject(text) }.getOrNull()
                    throw ApiException(res.code, o?.str("error") ?: "http_${res.code}", o?.str("message") ?: "DJFlix returned an error (HTTP ${res.code}).")
                }
                if (text.isBlank()) null else JSONTokener(text).nextValue()
            }
        } catch (e: IOException) {
            throw ApiException(0, "unreachable", "Cannot reach DJFlix on the PC. Check that the PC is on and connected to the same network.")
        } catch (e: org.json.JSONException) {
            throw ApiException(-1, "bad_response", "The server sent a reply this app could not read. Is that address really DJFlix?")
        }
    }

    suspend fun obj(path: String): JSONObject = call("GET", path) as? JSONObject ?: JSONObject()
    suspend fun list(path: String): List<JSONObject> = (call("GET", path) as? JSONArray).objs()
    suspend fun post(path: String, body: JSONObject? = null): JSONObject = call("POST", path, body) as? JSONObject ?: JSONObject()
    suspend fun put(path: String, body: JSONObject? = null): JSONObject = call("PUT", path, body) as? JSONObject ?: JSONObject()
    suspend fun patch(path: String, body: JSONObject): JSONObject = call("PATCH", path, body) as? JSONObject ?: JSONObject()
    suspend fun delete(path: String) { call("DELETE", path) }
}

fun enc(s: String): String = Uri.encode(s)

// ---- network detection and discovery --------------------------------------------------------
data class NetInfo(val connected: Boolean, val type: String, val ip: String?, val prefix: Int, val gateway: String?) {
    val subnet: String? get() = ip?.let { a ->
        val b = InetAddress.getByName(a).address; val mask = if (prefix == 0) 0 else (-1 shl (32 - prefix))
        val v = ((b[0].toInt() and 255) shl 24) or ((b[1].toInt() and 255) shl 16) or ((b[2].toInt() and 255) shl 8) or (b[3].toInt() and 255)
        val n = v and mask
        "${(n ushr 24) and 255}.${(n ushr 16) and 255}.${(n ushr 8) and 255}.${n and 255}/$prefix"
    }
    val broadcast: String? get() = ip?.let { a ->
        val b = InetAddress.getByName(a).address; val mask = if (prefix == 0) 0 else (-1 shl (32 - prefix))
        val v = ((b[0].toInt() and 255) shl 24) or ((b[1].toInt() and 255) shl 16) or ((b[2].toInt() and 255) shl 8) or (b[3].toInt() and 255)
        val n = v or mask.inv()
        "${(n ushr 24) and 255}.${(n ushr 16) and 255}.${(n ushr 8) and 255}.${n and 255}"
    }
}

@Suppress("DEPRECATION")
fun detectNetwork(ctx: Context): NetInfo {
    val cm = ctx.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
    for (net in cm.allNetworks) {
        val lp = cm.getLinkProperties(net) ?: continue
        val caps = cm.getNetworkCapabilities(net) ?: continue
        if (caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) continue
        val addr = lp.linkAddresses.firstOrNull { it.address is Inet4Address && !it.address.isLoopbackAddress } ?: continue
        val gw = lp.routes.firstOrNull { it.isDefaultRoute && it.gateway is Inet4Address }?.gateway?.hostAddress
        val type = when {
            caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "Wi-Fi"
            caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "Ethernet"
            else -> "Network"
        }
        return NetInfo(true, type, addr.address.hostAddress, addr.prefixLength, gw)
    }
    return NetInfo(false, "None", null, 0, null)
}

data class FoundServer(val name: String, val host: String, val port: Int, val serverId: String, val version: String)

/** Broadcasts a discovery probe on the LAN and collects replies from DJFlix hubs for a couple of seconds. */
suspend fun discoverServers(net: NetInfo): List<FoundServer> = withContext(Dispatchers.IO) {
    val found = LinkedHashMap<String, FoundServer>()
    runCatching {
        DatagramSocket().use { s ->
            s.broadcast = true; s.soTimeout = 500
            val msg = "DJFLIX_DISCOVER".toByteArray()
            val targets = listOfNotNull("255.255.255.255", net.broadcast).distinct()
            val buf = ByteArray(2048)
            val end = System.currentTimeMillis() + 2600
            var lastSend = 0L
            while (System.currentTimeMillis() < end) {
                if (System.currentTimeMillis() - lastSend > 800) {
                    lastSend = System.currentTimeMillis()
                    for (t in targets) runCatching { s.send(DatagramPacket(msg, msg.size, InetAddress.getByName(t), DISCOVERY_PORT)) }
                }
                try {
                    val p = DatagramPacket(buf, buf.size); s.receive(p)
                    val o = JSONObject(String(p.data, 0, p.length))
                    if (o.optString("service") == "djflix") {
                        val host = p.address.hostAddress ?: continue
                        found[o.optString("serverId", host)] = FoundServer(o.optString("name", "DJFlix"), host, o.optInt("port", DEFAULT_PORT), o.optString("serverId"), o.optString("version"))
                    }
                } catch (_: SocketTimeoutException) {
                } catch (_: org.json.JSONException) {
                }
            }
        }
    }
    found.values.toList()
}

fun deviceName(): String = "Fire TV (${Build.MODEL})"

// ---- app state & navigation -----------------------------------------------------------------
sealed class Screen {
    data class Setup(val startAt: String = "network") : Screen()
    object Profiles : Screen()
    object Main : Screen()
    data class Details(val id: Long) : Screen()
    data class Person(val name: String) : Screen()
    data class Player(val mediaId: Long, val episodeId: Long? = null, val startOver: Boolean = false) : Screen()
}

class AppState(ctx: Context) {
    val cfg = Config(ctx.applicationContext)
    val api = Api(cfg)
    /** Outlives screens so a progress save still completes while the player is closing. */
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    val stack = mutableStateListOf<Screen>()
    var profile by mutableStateOf<JSONObject?>(null)
    var toast by mutableStateOf<String?>(null)
    var tab by mutableStateOf("home")
    /** Bumped whenever lists may have changed, so screens underneath reload when they come back. */
    var dataVersion by mutableStateOf(0)

    val pid: Long get() = profile?.long("id") ?: cfg.profileId
    val prefs: JSONObject get() = profile?.obj("prefs") ?: JSONObject()

    fun go(s: Screen) { stack.add(s) }
    fun back(): Boolean { if (stack.size > 1) { stack.removeAt(stack.lastIndex); return true }; return false }
    fun reset(s: Screen) { stack.clear(); stack.add(s) }
    fun say(msg: String) { toast = msg; scope.launch { delay(3200); if (toast == msg) toast = null } }

    fun start() { reset(if (cfg.paired) Screen.Profiles else Screen.Setup()) }

    /** A revoked or forgotten pairing sends the user back to the pairing step instead of failing forever. */
    fun handle(e: Throwable) {
        if (e is ApiException && e.unpaired) { cfg.token = ""; say("This device is no longer paired. Pair it again."); reset(Screen.Setup("pair")) }
        else say(e.message ?: "Something went wrong.")
    }

    fun selectProfile(p: JSONObject) { profile = p; cfg.profileId = p.long("id"); tab = "home"; reset(Screen.Main) }

    suspend fun savePrefs(patch: JSONObject) {
        val updated = api.patch("/profiles/$pid", json("prefs" to patch))
        profile = updated
    }

    /** Sends a progress update that could not be delivered earlier, with its age so the hub can order it correctly. */
    suspend fun flushPendingProgress() {
        val raw = cfg.pendingProgress; if (raw.isEmpty()) return
        runCatching {
            val o = JSONObject(raw); val pid = o.getLong("pid"); val at = o.getLong("at")
            val body = o.getJSONObject("body").put("ageMs", System.currentTimeMillis() - at)
            api.put("/profiles/$pid/progress", body)
            cfg.pendingProgress = ""
        }.onFailure { if (it is ApiException && !it.unreachable) cfg.pendingProgress = "" }
    }
}

package com.djflix.tv

import android.content.Intent
import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.key
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

class MainActivity : ComponentActivity() {
    private lateinit var app: AppState

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN)
        app = AppState(this)
        app.start()
        handleLink(intent)
        setContent {
            MaterialTheme(colorScheme = darkColorScheme(primary = Accent, background = Bg, surface = Panel)) {
                Box(Modifier.fillMaxSize().background(Bg)) {
                    val screen = app.stack.lastOrNull() ?: Screen.Setup()
                    // Back pops one screen; at the root it falls through and leaves the app.
                    BackHandler(enabled = app.stack.size > 1 && screen !is Screen.Player) { app.back() }
                    key(app.stack.size, screen) {
                        when (screen) {
                            is Screen.Setup -> SetupWizard(app, screen)
                            is Screen.Profiles -> ProfilesScreen(app)
                            is Screen.Main -> MainScreen(app)
                            is Screen.Details -> DetailsScreen(app, screen.id)
                            is Screen.Person -> PersonScreen(app, screen.name)
                            is Screen.Player -> PlayerScreen(app, screen)
                        }
                    }
                    app.toast?.let { msg ->
                        Box(Modifier.align(Alignment.BottomCenter).padding(bottom = 40.dp).clip(RoundedCornerShape(10.dp)).background(Color(0xF2222226)).padding(horizontal = 22.dp, vertical = 12.dp)) {
                            Text(msg, color = Color.White, fontSize = 15.sp)
                        }
                    }
                }
                LaunchedEffect(Unit) { if (app.cfg.paired) app.flushPendingProgress() }
            }
        }
    }

    override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); handleLink(intent) }

    /** djflix://connect?host=…&port=…[&code=…] opens the wizard at the connection test with the address filled in. */
    private fun handleLink(intent: Intent?) {
        val uri = intent?.data ?: return
        if (uri.scheme != "djflix" || uri.host != "connect") return
        val host = uri.getQueryParameter("host")?.takeIf { Regex("""^\d{1,3}(\.\d{1,3}){3}$""").matches(it) } ?: return
        val port = uri.getQueryParameter("port")?.toIntOrNull()?.takeIf { it in 1024..65535 } ?: DEFAULT_PORT
        val code = uri.getQueryParameter("code")?.takeIf { Regex("""^\d{6}$""").matches(it) }
        app.reset(Screen.Setup("test", host, port, code))
    }
}

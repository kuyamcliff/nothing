package com.clipfx.share

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

private val Light = lightColorScheme(
    primary = Color(0xFF2A2A2F), onPrimary = Color.White,
    primaryContainer = Color(0xFFE4E4E7), onPrimaryContainer = Color(0xFF1C1C1F),
    secondary = Color(0xFF55555C), onSecondary = Color.White,
    secondaryContainer = Color(0xFFE4E4E7), onSecondaryContainer = Color(0xFF1C1C1F),
    background = Color(0xFFF2F2F3), onBackground = Color(0xFF1C1C1F),
    surface = Color(0xFFFFFFFF), onSurface = Color(0xFF1C1C1F),
    surfaceVariant = Color(0xFFECECEE), onSurfaceVariant = Color(0xFF55555C),
    surfaceContainer = Color(0xFFF7F7F8), surfaceContainerHigh = Color(0xFFF0F0F2), surfaceContainerHighest = Color(0xFFE8E8EB),
    surfaceContainerLow = Color(0xFFFAFAFB), surfaceContainerLowest = Color.White,
    outline = Color(0xFFCFCFD4), outlineVariant = Color(0xFFE1E1E4),
    error = Color(0xFFC33A32), onError = Color.White, errorContainer = Color(0xFFFBECEB), onErrorContainer = Color(0xFF8A1F18),
)

private val Dark = darkColorScheme(
    primary = Color(0xFFE6E6E9), onPrimary = Color(0xFF18181B),
    primaryContainer = Color(0xFF3A3A40), onPrimaryContainer = Color(0xFFECECEF),
    secondary = Color(0xFFA6A6AE), onSecondary = Color(0xFF18181B),
    secondaryContainer = Color(0xFF34343A), onSecondaryContainer = Color(0xFFECECEF),
    background = Color(0xFF18181B), onBackground = Color(0xFFECECEF),
    surface = Color(0xFF202023), onSurface = Color(0xFFECECEF),
    surfaceVariant = Color(0xFF2E2E33), onSurfaceVariant = Color(0xFFA6A6AE),
    surfaceContainer = Color(0xFF242428), surfaceContainerHigh = Color(0xFF2A2A2E), surfaceContainerHighest = Color(0xFF323237),
    surfaceContainerLow = Color(0xFF1E1E21), surfaceContainerLowest = Color(0xFF141416),
    outline = Color(0xFF404047), outlineVariant = Color(0xFF313136),
    error = Color(0xFFEF7D74), onError = Color(0xFF18181B), errorContainer = Color(0xFF36201F), onErrorContainer = Color(0xFFF5B8B3),
)

object Tone {
    @Composable fun warn() = if (isDark()) Color(0xFFE2B05A) else Color(0xFFA8660B)
    @Composable fun ok() = if (isDark()) Color(0xFF6CC28F) else Color(0xFF2F7D4F)
    @Composable fun isDark() = MaterialTheme.colorScheme.background == Color(0xFF18181B)
}

@Composable
fun ShareTheme(theme: String, content: @Composable () -> Unit) {
    val dark = when (theme) { "dark" -> true; "light" -> false; else -> isSystemInDarkTheme() }
    val base = Typography()
    MaterialTheme(
        colorScheme = if (dark) Dark else Light,
        typography = base.copy(
            titleLarge = base.titleLarge.copy(fontWeight = FontWeight.SemiBold, fontSize = 20.sp),
            titleMedium = base.titleMedium.copy(fontWeight = FontWeight.SemiBold),
            labelLarge = base.labelLarge.copy(fontWeight = FontWeight.SemiBold),
        ),
        content = content,
    )
}

val SectionLabel: TextStyle @Composable get() = MaterialTheme.typography.labelMedium.copy(
    fontWeight = FontWeight.SemiBold, letterSpacing = 0.8.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)

package com.clipfx.share.core

import kotlinx.serialization.json.*

val json = Json { ignoreUnknownKeys = true; explicitNulls = false; encodeDefaults = true; isLenient = true }

fun JsonObject.str(k: String): String? = (this[k] as? JsonPrimitive)?.takeIf { it !is JsonNull }?.contentOrNull
fun JsonObject.long(k: String): Long = (this[k] as? JsonPrimitive)?.longOrNull ?: 0L
fun JsonObject.bool(k: String): Boolean = (this[k] as? JsonPrimitive)?.booleanOrNull ?: false

fun ok(vararg extra: Pair<String, JsonElement>) = JsonObject(mapOf("ok" to JsonPrimitive(true)) + extra)
fun err(e: String) = JsonObject(mapOf("ok" to JsonPrimitive(false), "error" to JsonPrimitive(e)))

inline fun <reified T> T.toJson(): JsonElement = json.encodeToJsonElement(this)
inline fun <reified T> JsonElement.parse(): T = json.decodeFromJsonElement(this)

fun haveJson(items: List<ItemInfo>, have: Array<RangeSet>): JsonArray = JsonArray(
    items.filter { it.type == ItemType.FILE && have[it.i].covered > 0 }.map {
        buildJsonObject {
            put("i", it.i)
            put("r", JsonArray(have[it.i].toList().map { r -> JsonArray(r.map { v -> JsonPrimitive(v) }) }))
        }
    }
)

package com.example.phoneunlock

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONArray

data class AndroidRelease(val tag: String, val downloadUrl: String)

class ReleaseUpdateChecker(
    private val client: OkHttpClient = OkHttpClient()
) {
    suspend fun findUpdate(currentVersion: String): AndroidRelease? = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url("https://api.github.com/repos/blossom0948/windowslogin/releases?per_page=10")
            .header("Accept", "application/vnd.github+json")
            .header("User-Agent", "PhoneUnlock-Android/$currentVersion")
            .build()

        client.newCall(request).execute().use { response ->
            check(response.isSuccessful) { "업데이트 서버 응답 오류: HTTP ${response.code}" }
            val releases = JSONArray(response.body?.string() ?: error("업데이트 응답이 비어 있습니다."))
            for (releaseIndex in 0 until releases.length()) {
                val release = releases.getJSONObject(releaseIndex)
                if (release.optBoolean("draft")) continue
                val assets = release.getJSONArray("assets")
                for (assetIndex in 0 until assets.length()) {
                    val asset = assets.getJSONObject(assetIndex)
                    if (asset.getString("name") != "PhoneUnlock-Android.apk") continue
                    val tag = release.getString("tag_name")
                    if (compareVersions(tag, currentVersion) > 0) {
                        return@withContext AndroidRelease(tag, asset.getString("browser_download_url"))
                    }
                    return@withContext null
                }
            }
            null
        }
    }

    private fun compareVersions(left: String, right: String): Int {
        val leftVersion = parseVersion(left)
        val rightVersion = parseVersion(right)
        repeat(maxOf(leftVersion.core.size, rightVersion.core.size)) { index ->
            val comparison = leftVersion.core.getOrElse(index) { 0 }
                .compareTo(rightVersion.core.getOrElse(index) { 0 })
            if (comparison != 0) return comparison
        }

        if (leftVersion.preRelease.isEmpty() || rightVersion.preRelease.isEmpty()) {
            return rightVersion.preRelease.size.compareTo(leftVersion.preRelease.size)
        }

        repeat(maxOf(leftVersion.preRelease.size, rightVersion.preRelease.size)) { index ->
            if (index >= leftVersion.preRelease.size) return -1
            if (index >= rightVersion.preRelease.size) return 1
            val leftPart = leftVersion.preRelease[index]
            val rightPart = rightVersion.preRelease[index]
            val leftNumber = leftPart.toIntOrNull()
            val rightNumber = rightPart.toIntOrNull()
            if (leftNumber != null && rightNumber != null && leftNumber != rightNumber) {
                return leftNumber.compareTo(rightNumber)
            }
            if ((leftNumber != null) != (rightNumber != null)) return if (leftNumber != null) -1 else 1
            val comparison = leftPart.compareTo(rightPart, ignoreCase = true)
            if (comparison != 0) return comparison
        }
        return 0
    }

    private fun parseVersion(value: String): ParsedVersion {
        val normalized = value.trim().trimStart('v', 'V').substringBefore('+')
        val coreAndPreRelease = normalized.split('-', limit = 2)
        val core = coreAndPreRelease[0].split('.').map { it.toIntOrNull() ?: 0 }
        val preRelease = coreAndPreRelease.getOrNull(1)?.split('.') ?: emptyList()
        return ParsedVersion(core, preRelease)
    }

    private data class ParsedVersion(val core: List<Int>, val preRelease: List<String>)
}

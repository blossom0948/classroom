package com.example.phoneunlock

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.URI
import java.util.concurrent.CancellationException
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONArray
import org.json.JSONObject

data class AndroidRelease(val tag: String, val downloadUrl: String)

class ReleaseUpdateChecker(
    private val client: OkHttpClient = OkHttpClient()
) {
    companion object {
        private const val UPDATE_MANIFEST_URL =
            "https://raw.githubusercontent.com/blossom0948/windowslogin/main/update.json"
        private const val RELEASES_API_URL =
            "https://api.github.com/repos/blossom0948/windowslogin/releases?per_page=10"

        fun isSafeDownloadUrl(value: String): Boolean = try {
            val uri = URI(value)
            val path = uri.path.orEmpty()
            uri.scheme.equals("https", ignoreCase = true)
                && uri.host.equals("github.com", ignoreCase = true)
                && path.startsWith("/blossom0948/windowslogin/releases/download/", ignoreCase = true)
                && path.endsWith("/PhoneUnlock-Android.apk", ignoreCase = true)
        } catch (_: Exception) {
            false
        }
    }

    suspend fun findUpdate(currentVersion: String): AndroidRelease? = withContext(Dispatchers.IO) {
        // GitHub's unauthenticated REST API is rate-limited per public IP. The
        // small repository-owned manifest is the primary source so update
        // checks keep working on mobile networks and behind shared NATs.
        try {
            return@withContext findFromManifest(currentVersion)
        } catch (manifestException: Exception) {
            if (manifestException is CancellationException) throw manifestException
            // Keep the API path as a compatibility fallback for older
            // manifests/mirrors. The caller will show a useful error if both
            // public sources are unavailable.
        }

        findFromGitHubApi(currentVersion)
    }

    private fun findFromManifest(currentVersion: String): AndroidRelease? {
        val request = Request.Builder()
            .url(UPDATE_MANIFEST_URL)
            .header("Accept", "application/json")
            .header("Cache-Control", "no-cache")
            .header("User-Agent", "PhoneUnlock-Android/$currentVersion")
            .build()

        client.newCall(request).execute().use { response ->
            val body = response.body?.string().orEmpty()
            if (!response.isSuccessful) {
                throw IOException("업데이트 매니페스트 응답 오류: HTTP ${response.code}")
            }
            if (body.isBlank()) {
                throw IOException("업데이트 매니페스트가 비어 있습니다.")
            }

            val root = JSONObject(body)
            val android = root.optJSONObject("android")
                ?: throw IOException("업데이트 매니페스트에 Android 정보가 없습니다.")
            val tag = android.optString("tag").ifBlank { root.optString("tag") }
            val version = android.optString("version").ifBlank { root.optString("version") }
            val downloadUrl = safeDownloadUrl(android.optString("downloadUrl"))
            if (tag.isBlank() || version.isBlank()) {
                throw IOException("업데이트 매니페스트의 버전 정보가 없습니다.")
            }
            if (compareVersions(version, currentVersion) <= 0) {
                return null
            }
            return AndroidRelease(tag, downloadUrl)
        }
    }

    private fun findFromGitHubApi(currentVersion: String): AndroidRelease? {
        val request = Request.Builder()
            .url(RELEASES_API_URL)
            .header("Accept", "application/vnd.github+json")
            .header("User-Agent", "PhoneUnlock-Android/$currentVersion")
            .build()

        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) {
                throw IOException(
                    if (response.code == 403) {
                        "업데이트 서버 요청이 잠시 제한되었습니다. 잠시 후 다시 시도해 주세요."
                    } else {
                        "업데이트 서버 응답 오류: HTTP ${response.code}"
                    }
                )
            }
            val releases = JSONArray(response.body?.string().orEmpty())
            var newest: AndroidRelease? = null
            for (releaseIndex in 0 until releases.length()) {
                val release = releases.getJSONObject(releaseIndex)
                if (release.optBoolean("draft")) continue
                val assets = release.getJSONArray("assets")
                for (assetIndex in 0 until assets.length()) {
                    val asset = assets.getJSONObject(assetIndex)
                    if (asset.getString("name") != "PhoneUnlock-Android.apk") continue
                    val tag = release.getString("tag_name")
                    val candidate = AndroidRelease(
                        tag,
                        safeDownloadUrl(asset.getString("browser_download_url")),
                    )
                    if (compareVersions(candidate.tag, currentVersion) > 0
                        && (newest == null || compareVersions(candidate.tag, newest!!.tag) > 0)
                    ) {
                        newest = candidate
                    }
                }
            }
            newest
        }
    }

    private fun safeDownloadUrl(value: String): String {
        val uri = try {
            URI(value)
        } catch (exception: Exception) {
            throw IOException("업데이트 주소를 해석할 수 없습니다.", exception)
        }
        val path = uri.path.orEmpty()
        if (!uri.scheme.equals("https", ignoreCase = true)
            || !uri.host.equals("github.com", ignoreCase = true)
            || !path.startsWith("/blossom0948/windowslogin/releases/download/", ignoreCase = true)
            || !path.endsWith("/PhoneUnlock-Android.apk", ignoreCase = true)
        ) {
            throw IOException("안전하지 않은 업데이트 주소가 거부되었습니다.")
        }
        return uri.toString()
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

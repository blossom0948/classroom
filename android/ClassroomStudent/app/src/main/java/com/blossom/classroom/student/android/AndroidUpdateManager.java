package com.blossom.classroom.student.android;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.security.MessageDigest;
import java.util.Locale;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import okhttp3.Call;
import okhttp3.Callback;
import okhttp3.HttpUrl;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.ResponseBody;

/** Checks the public Android release feed and downloads only verified Classroom APK assets. */
final class AndroidUpdateManager {
    private static final String RELEASES_URL =
            "https://api.github.com/repos/blossom0948/classroom/releases?per_page=100";
    private static final String TAG_PREFIX = "android-student-v";
    private static final String APK_NAME = "Classroom-Student-Android.apk";
    private static final long MAX_APK_BYTES = 128L * 1024L * 1024L;
    private static final int MAX_RELEASE_PAGES = 10;
    private static final Pattern UPDATE_METADATA = Pattern.compile(
            "(?m)^ANDROID_UPDATE=(\\{[^\\r\\n]+\\})\\s*$");

    interface CheckCallback {
        void onSuccess(Update update);
        void onFailure(String reason);
    }

    interface DownloadCallback {
        void onSuccess(File apk);
        void onFailure(String reason);
    }

    static final class Update {
        final int versionCode;
        final String versionName;
        final String downloadUrl;
        final String sha256;
        final String releasePage;

        Update(int versionCode, String versionName, String downloadUrl, String sha256, String releasePage) {
            this.versionCode = versionCode;
            this.versionName = versionName;
            this.downloadUrl = downloadUrl;
            this.sha256 = sha256;
            this.releasePage = releasePage;
        }
    }

    private final OkHttpClient client;

    AndroidUpdateManager(OkHttpClient client) {
        this.client = client;
    }

    void checkForUpdate(int installedVersionCode, CheckCallback callback) {
        loadReleasePage(installedVersionCode, 1, callback);
    }

    private void loadReleasePage(int installedVersionCode, int page, CheckCallback callback) {
        Request request = new Request.Builder()
                .url(RELEASES_URL + "&page=" + page)
                .header("Accept", "application/vnd.github+json")
                .header("X-GitHub-Api-Version", "2022-11-28")
                .header("User-Agent", "Classroom-Student-Android-Updater")
                .get()
                .build();
        client.newCall(request).enqueue(new Callback() {
            @Override
            public void onFailure(Call call, java.io.IOException exception) {
                callback.onFailure("업데이트 서버에 연결하지 못했습니다.");
            }

            @Override
            public void onResponse(Call call, Response response) {
                try (Response closeable = response) {
                    if (!closeable.isSuccessful() || closeable.body() == null) {
                        callback.onFailure("업데이트 정보를 확인하지 못했습니다.");
                        return;
                    }
                    JSONArray releases = new JSONArray(closeable.body().string());
                    for (int index = 0; index < releases.length(); index++) {
                        JSONObject release = releases.optJSONObject(index);
                        if (release == null || !release.optString("tag_name", "").startsWith(TAG_PREFIX)) continue;
                        Update candidate = parseRelease(release);
                        callback.onSuccess(candidate != null && candidate.versionCode > installedVersionCode
                                ? candidate : null);
                        return;
                    }
                    if (releases.length() == 100 && page < MAX_RELEASE_PAGES) {
                        loadReleasePage(installedVersionCode, page + 1, callback);
                    } else {
                        callback.onSuccess(null);
                    }
                } catch (Exception exception) {
                    callback.onFailure("업데이트 정보 형식이 올바르지 않습니다.");
                }
            }
        });
    }

    void download(Update update, File destination, DownloadCallback callback) {
        HttpUrl url = HttpUrl.parse(update.downloadUrl);
        if (url == null || !"https".equals(url.scheme()) || !"github.com".equalsIgnoreCase(url.host())) {
            callback.onFailure("안전하지 않은 업데이트 주소를 차단했습니다.");
            return;
        }

        Request request = new Request.Builder()
                .url(url)
                .header("Accept", "application/octet-stream")
                .header("User-Agent", "Classroom-Student-Android-Updater")
                .get()
                .build();
        client.newCall(request).enqueue(new Callback() {
            @Override
            public void onFailure(Call call, java.io.IOException exception) {
                callback.onFailure("APK를 내려받지 못했습니다. 네트워크를 확인해 주세요.");
            }

            @Override
            public void onResponse(Call call, Response response) {
                File temporary = new File(destination.getParentFile(), destination.getName() + ".part");
                try (Response closeable = response) {
                    if (!closeable.isSuccessful()) throw new IllegalStateException("다운로드 서버 응답 오류");
                    ResponseBody body = closeable.body();
                    if (body == null || body.contentLength() > MAX_APK_BYTES) {
                        throw new IllegalStateException("APK 크기가 허용 범위를 벗어났습니다.");
                    }

                    File parent = destination.getParentFile();
                    if (parent == null || (!parent.exists() && !parent.mkdirs())) {
                        throw new IllegalStateException("업데이트 저장 공간을 준비하지 못했습니다.");
                    }

                    MessageDigest digest = MessageDigest.getInstance("SHA-256");
                    long total = 0;
                    byte[] buffer = new byte[16 * 1024];
                    try (InputStream input = body.byteStream(); FileOutputStream output = new FileOutputStream(temporary)) {
                        int read;
                        while ((read = input.read(buffer)) != -1) {
                            total += read;
                            if (total > MAX_APK_BYTES) throw new IllegalStateException("APK 크기가 허용 범위를 벗어났습니다.");
                            digest.update(buffer, 0, read);
                            output.write(buffer, 0, read);
                        }
                        output.getFD().sync();
                    }

                    String expected = normalizeDigest(update.sha256);
                    if (expected.isBlank() || !expected.equals(toHex(digest.digest()))) {
                        throw new SecurityException("APK 체크섬이 일치하지 않습니다.");
                    }
                    if (destination.exists() && !destination.delete()) {
                        throw new IllegalStateException("기존 업데이트 파일을 교체하지 못했습니다.");
                    }
                    if (!temporary.renameTo(destination)) {
                        throw new IllegalStateException("업데이트 파일을 저장하지 못했습니다.");
                    }
                    callback.onSuccess(destination);
                } catch (Exception exception) {
                    if (temporary.exists()) temporary.delete();
                    callback.onFailure(exception instanceof SecurityException
                            ? "APK 무결성 확인에 실패해 설치를 중단했습니다."
                            : exception.getMessage() == null ? "APK 다운로드에 실패했습니다." : exception.getMessage());
                }
            }
        });
    }

    private static Update parseRelease(JSONObject release) {
        if (release == null || release.optBoolean("draft", true)) return null;
        String tag = release.optString("tag_name", "");
        if (!tag.startsWith(TAG_PREFIX)) return null;

        Matcher matcher = UPDATE_METADATA.matcher(release.optString("body", ""));
        if (!matcher.find()) return null;
        try {
            JSONObject metadata = new JSONObject(matcher.group(1));
            int versionCode = metadata.optInt("versionCode", 0);
            String versionName = metadata.optString("versionName", "").trim();
            if (versionCode < 1 || versionName.isBlank()) return null;

            JSONArray assets = release.optJSONArray("assets");
            if (assets == null) return null;
            for (int index = 0; index < assets.length(); index++) {
                JSONObject asset = assets.optJSONObject(index);
                if (asset == null || !APK_NAME.equals(asset.optString("name"))) continue;
                String url = asset.optString("browser_download_url", "");
                String digest = asset.optString("digest", "");
                HttpUrl parsed = HttpUrl.parse(url);
                if (parsed == null || !"https".equals(parsed.scheme())
                        || !"github.com".equalsIgnoreCase(parsed.host())
                        || !digest.toLowerCase(Locale.ROOT).startsWith("sha256:")) return null;
                return new Update(versionCode, versionName, url, digest.substring("sha256:".length()),
                        release.optString("html_url", ""));
            }
        } catch (Exception ignored) {
            return null;
        }
        return null;
    }

    private static String normalizeDigest(String value) {
        String digest = value == null ? "" : value.trim().toLowerCase(Locale.ROOT);
        return digest.startsWith("sha256:") ? digest.substring("sha256:".length()) : digest;
    }

    private static String toHex(byte[] bytes) {
        StringBuilder result = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) result.append(String.format(Locale.ROOT, "%02x", value & 0xff));
        return result.toString();
    }
}

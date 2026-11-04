package com.blossom.classroom.student.android;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;

import org.json.JSONObject;

import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/** Stores the device bearer token encrypted with a non-exportable Android Keystore key. */
public final class SecureDeviceStore {
    private static final String PREFS = "classroom_secure_device";
    private static final String ENROLLMENT = "enrollment_blob";
    private static final String KEY_ALIAS = "classroom.student.device-token.v1";
    private static final int GCM_TAG_BITS = 128;

    private SecureDeviceStore() { }

    public static void save(Context context, Enrollment enrollment) throws Exception {
        JSONObject json = new JSONObject()
                .put("serverOrigin", enrollment.serverOrigin)
                .put("deviceId", enrollment.deviceId)
                .put("deviceToken", enrollment.deviceToken)
                .put("schoolId", enrollment.schoolId)
                .put("classId", enrollment.classId)
                .put("studentId", enrollment.studentId)
                .put("issuedAtUtc", enrollment.issuedAtUtc);

        Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey());
        byte[] iv = cipher.getIV();
        byte[] encrypted = cipher.doFinal(json.toString().getBytes(StandardCharsets.UTF_8));
        ByteBuffer packed = ByteBuffer.allocate(1 + iv.length + encrypted.length);
        packed.put((byte) iv.length).put(iv).put(encrypted);
        String encoded = Base64.encodeToString(packed.array(), Base64.NO_WRAP);
        prefs(context).edit().putString(ENROLLMENT, encoded).apply();
    }

    public static Enrollment load(Context context) {
        String encoded = prefs(context).getString(ENROLLMENT, null);
        if (encoded == null || encoded.isBlank()) return null;

        try {
            byte[] packed = Base64.decode(encoded, Base64.NO_WRAP);
            ByteBuffer buffer = ByteBuffer.wrap(packed);
            int ivLength = Byte.toUnsignedInt(buffer.get());
            if (ivLength < 12 || ivLength > 16 || buffer.remaining() <= ivLength) {
                throw new IllegalStateException("Invalid encrypted enrollment data.");
            }
            byte[] iv = new byte[ivLength];
            buffer.get(iv);
            byte[] encrypted = new byte[buffer.remaining()];
            buffer.get(encrypted);

            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, getOrCreateKey(), new GCMParameterSpec(GCM_TAG_BITS, iv));
            JSONObject json = new JSONObject(new String(cipher.doFinal(encrypted), StandardCharsets.UTF_8));
            String deviceId = json.optString("deviceId", "");
            String token = json.optString("deviceToken", "");
            String origin = json.optString("serverOrigin", "");
            if (deviceId.isBlank() || token.isBlank() || origin.isBlank()) {
                throw new IllegalStateException("Enrollment data is incomplete.");
            }
            return new Enrollment(
                    origin,
                    deviceId,
                    token,
                    json.optString("schoolId", ""),
                    json.optString("classId", ""),
                    json.optString("studentId", ""),
                    json.optString("issuedAtUtc", ""));
        } catch (Exception exception) {
            // A Keystore reset or corrupted ciphertext must not leave an unusable token around.
            clear(context);
            return null;
        }
    }

    public static boolean hasEnrollment(Context context) {
        return prefs(context).contains(ENROLLMENT);
    }

    public static void clear(Context context) {
        prefs(context).edit().remove(ENROLLMENT).apply();
    }

    private static SharedPreferences prefs(Context context) {
        return context.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    private static SecretKey getOrCreateKey() throws Exception {
        KeyStore keyStore = KeyStore.getInstance("AndroidKeyStore");
        keyStore.load(null);
        java.security.Key existing = keyStore.getKey(KEY_ALIAS, null);
        if (existing instanceof SecretKey secretKey) return secretKey;

        KeyGenerator generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
        generator.init(new KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .setRandomizedEncryptionRequired(true)
                .build());
        return generator.generateKey();
    }

    public static final class Enrollment {
        public final String serverOrigin;
        public final String deviceId;
        public final String deviceToken;
        public final String schoolId;
        public final String classId;
        public final String studentId;
        public final String issuedAtUtc;

        public Enrollment(
                String serverOrigin,
                String deviceId,
                String deviceToken,
                String schoolId,
                String classId,
                String studentId,
                String issuedAtUtc) {
            this.serverOrigin = serverOrigin;
            this.deviceId = deviceId;
            this.deviceToken = deviceToken;
            this.schoolId = schoolId;
            this.classId = classId;
            this.studentId = studentId;
            this.issuedAtUtc = issuedAtUtc;
        }
    }
}

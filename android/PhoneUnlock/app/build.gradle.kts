plugins {
    id("com.android.application")
}

val releaseKeystorePath = providers.environmentVariable("PHONE_UNLOCK_KEYSTORE_PATH").orNull
val releaseKeystorePassword = providers.environmentVariable("PHONE_UNLOCK_KEYSTORE_PASSWORD").orNull
val releaseKeyAlias = providers.environmentVariable("PHONE_UNLOCK_KEY_ALIAS").orNull
val releaseKeyPassword = providers.environmentVariable("PHONE_UNLOCK_KEY_PASSWORD").orNull
val hasReleaseSigning = listOf(
    releaseKeystorePath,
    releaseKeystorePassword,
    releaseKeyAlias,
    releaseKeyPassword
).all { !it.isNullOrBlank() }

android {
    namespace = "com.example.phoneunlock"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.example.phoneunlock"
        minSdk = 30
        targetSdk = 36
        versionCode = 17
        versionName = "0.4.0-beta.14"
    }

    signingConfigs {
        if (hasReleaseSigning) {
            create("phoneUnlockRelease") {
                storeFile = file(releaseKeystorePath!!)
                storePassword = releaseKeystorePassword
                keyAlias = releaseKeyAlias
                keyPassword = releaseKeyPassword
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (hasReleaseSigning) {
                signingConfig = signingConfigs.getByName("phoneUnlockRelease")
            }
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    buildFeatures {
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.17.0")
    implementation("androidx.appcompat:appcompat:1.7.1")
    implementation("androidx.biometric:biometric:1.1.0")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.9.4")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    implementation("com.google.android.material:material:1.13.0")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
}

gradle.taskGraph.whenReady {
    if (allTasks.any { it.name.contains("Release", ignoreCase = true) } && !hasReleaseSigning) {
        throw GradleException("Release APK requires the PHONE_UNLOCK_KEYSTORE_* environment variables.")
    }
}

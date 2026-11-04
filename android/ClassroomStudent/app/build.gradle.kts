plugins {
    id("com.android.application")
}

android {
    namespace = "com.blossom.classroom.student.android"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.blossom.classroom.student.android"
        minSdk = 26
        targetSdk = 35
        versionCode = 6
        versionName = "0.2.4-alpha"
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
    implementation("androidx.dynamicanimation:dynamicanimation:1.0.0")
    implementation("com.google.android.material:material:1.13.0")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
}

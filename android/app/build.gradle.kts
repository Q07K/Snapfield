plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.snapfield.receiver"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.snapfield.receiver"
        minSdk = 26 // adaptive icons + dispatchGesture floor
        targetSdk = 34
        // CI passes the release tag in; local builds get a dev marker.
        versionName = (project.findProperty("versionName") as String?) ?: "0.0.0-dev"
        versionCode = (project.findProperty("versionCode") as String?)?.toIntOrNull() ?: 1
    }

    // Release builds sign with the real keystore when CI provides one (repo
    // secret), and fall back to the debug key otherwise — either way the APK
    // is installable. NOTE: debug-key builds change signature per machine, so
    // updating over them needs an uninstall first.
    signingConfigs {
        create("release") {
            // CI env from an unset GitHub secret is an EMPTY STRING, not null —
            // treat blank as absent or the alias/password silently break.
            fun env(name: String) = System.getenv(name)?.takeIf { it.isNotBlank() }
            val ks = env("ANDROID_KEYSTORE_FILE")
            if (ks != null) {
                storeFile = file(ks)
                storeType = "pkcs12"
                storePassword = env("ANDROID_KEYSTORE_PASSWORD")
                keyAlias = env("ANDROID_KEY_ALIAS") ?: "snapfield"
                keyPassword = env("ANDROID_KEY_PASSWORD") ?: env("ANDROID_KEYSTORE_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = if (System.getenv("ANDROID_KEYSTORE_FILE") != null)
                signingConfigs.getByName("release")
            else
                signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    // Deliberately zero external dependencies while this is a shell — the
    // protocol port will add kotlinx-coroutines when it lands.
}

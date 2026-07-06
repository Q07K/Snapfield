// Snapfield Android receiver — lets a phone/tablet join the physical plane as a
// receive-only device. Early scaffold: the app shell builds and installs; the
// protocol port (encrypted TCP + beacon) and input injection come next.
plugins {
    id("com.android.application") version "8.5.2" apply false
    id("org.jetbrains.kotlin.android") version "1.9.24" apply false
}

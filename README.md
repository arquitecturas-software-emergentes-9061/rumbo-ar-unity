# Rumbo AR prototype (Android)

Unity 6000.3.26f1, URP, AR Foundation/ARCore 6.7.1, XR Plug-in Management 4.7.0. Open `Assets/Scenes/RumboArPrototype.unity`. This is a technical demo: the yellow arrow is a manually selected direction, not a BLE position or backend route.

## Export into Flutter

Install **Android Build Support** for Unity 6000.3.26f1 (including SDK/NDK/OpenJDK) in Unity Hub. In Unity choose **Rumbo → Export Android library**. This switches the build target to Android and generates `Builds/Android/unityLibrary` beside the Flutter repository. Mobile's Gradle settings include that module only when it exists. Confirm that directory exists before building `rumbo-mobile-application` with `flutter build apk --debug`; an APK built without it contains Flutter and BLE but not Unity. Run the integrated APK on an ARCore-capable Android phone (API 25+, ARM64).

The demo is reached through Flutter **Tu ruta → Abrir prueba AR** in debug builds. Choose a heading in Flutter. Unity requests camera permission, shows tracked horizontal surfaces, places an anchored arrow when a tracked plane is tapped, turns it with the left/right buttons, and returns to Flutter with **Exit to Flutter**. If the library is absent, Flutter displays a controlled error. The Unity activity reports status and UTC timestamps through `com.uitopic.rumbo/ar/events` and appends them to `rumbo_ar_tracking.csv` under Unity's `Application.persistentDataPath`. After returning to Flutter, choose **Exportar registro AR** to save that file. The BLE channel remains separate.

## Physical validation

On a real ARCore-capable phone, test: permissions granted/denied; no surface; floor/table detection; anchored arrow and direction buttons; normal, limited and lost tracking while changing light and moving or occluding the camera; disappearance and recovery of the arrow; exit and re-entry into Flutter; BLE capture continuity if active. Collect device model, Android/ARCore versions, timestamps and observed outcomes. No physical AR test has been run in this repository.

# Migration to Jetpack Compose 1.13.0 & Material 3 Expressive 1.5.0 (Stable)

## 1. Executive Summary

This document details the architectural migration of the **Forkdesk Android Client** (`android/app`) to Google's official stable milestone of **Compose Material 3 version 1.5.0** and the **Jetpack Compose November '26 Release (version 1.13.0 via BOM `2026.11.01`)**.

* **Target Compose BOM:** `androidx.compose:compose-bom:2026.11.01` (was `compose-bom-beta:2026.11.00`)
* **Target Material 3:** `androidx.compose.material3:material3:1.5.0` Stable
* **Theme Motion:** Explicitly wired `MotionScheme.expressive()` into `MaterialExpressiveTheme` in `Ui.kt`.
* **Experimental Opt-In Cleanups:** Removed `@OptIn(ExperimentalMaterial3ExpressiveApi::class)` from `MainActivity.kt` as `FloatingActionButtonMenu` and `ToggleFloatingActionButton` have graduated to stable.
* **CI/CD Automation:** Updated workflows (`build-android.yml` and `test.yml`) so that pushing commits to any branch triggers automatic Android builds and APK packaging.

---

## 2. Whole Thinking Process & Architectural Rationale

### A. Transition from Compose Beta BOM to Official Stable BOM
`forkdesk/android/app/build.gradle.kts` was previously consuming `androidx.compose:compose-bom-beta:2026.11.00`.
1. Beta BOMs introduce instability risks, moving targets, and can cause unpredictable transient compiler warnings.
2. The November 18, 2026 release of Compose BOM `2026.11.01` locks in stable Compose 1.13.0 across UI, Foundation, Runtime, and Animation.
3. Explicitly declaring `androidx.compose.material3:material3:1.5.0` guarantees that newly graduated Expressive APIs are loaded immediately without awaiting unpinned BOM resolutions.

### B. Activating Physics-Based Spring Motion (`MotionScheme.expressive()`)
Forkdesk's `Ui.kt` previously initialized `MaterialExpressiveTheme` with only `colorScheme = scheme`.
* In Compose Material 3 1.5.0, omitting the `motionScheme` parameter defaults internally, but explicitly providing `motionScheme = MotionScheme.expressive()` binds all theme-level transitions (modal drawers, sheet elevations, and action transitions) directly into spring tokens.
* Spatial transitions (sheet drag, FAB expansion, dialog scales) now smoothly bounce and retarget on mid-gesture interruptions, creating a tactile, physical interaction model matching modern desktop-to-mobile companion standards.

### C. Deprecating Experimental Opt-Ins for Graduated APIs
In `MainActivity.kt`, the root navigation and FAB quick actions use `FloatingActionButtonMenu`, `FloatingActionButtonMenuItem`, and `ToggleFloatingActionButton`.
* These components required `@OptIn(ExperimentalMaterial3ExpressiveApi::class)` in earlier pre-releases.
* With M3 1.5.0, Google promoted the entire FAB menu and button group layout specification to fully stable. Removing the `@OptIn` cleans up compiler noise and ensures that only genuinely experimental APIs (such as `ModalBottomSheet` in `ExperimentalMaterial3Api`) remain marked.

### D. Automated CI/CD on Every Push
Previously:
* `build-android.yml` was configured only with `workflow_dispatch`, requiring developers to manually trigger a release run from the GitHub Actions tab.
* `test.yml` only ran on pushes to `main`.

**The Problem:** Any commit pushed to development or feature branches did not produce build feedback or verified APK artifacts.
**The Fix:** Added `push: branches: ['**']` to both `build-android.yml` and `test.yml`. Whenever any commit is pushed, GitHub Actions immediately compiles standard, full, and F-Droid variants and packages downloadable artifacts.

---

## 3. Detailed Changes Implemented

### 1. Build Dependencies (`android/app/build.gradle.kts`)
```kotlin
dependencies {
    implementation(platform("androidx.compose:compose-bom:2026.11.01"))
    implementation("androidx.compose.material3:material3:1.5.0")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.activity:activity-compose:1.13.0")
    // ...
}
```

### 2. Design System & Theme Modernization (`android/app/.../Ui.kt`)
* Imported `androidx.compose.material3.MotionScheme`.
* Updated `Theme(content)`:
```kotlin
CompositionLocalProvider(LocalPalette provides p) {
    MaterialExpressiveTheme(
        colorScheme = scheme,
        motionScheme = MotionScheme.expressive(),
        content = content
    )
}
```

### 3. Cleanup of Experimental Annotations (`android/app/.../MainActivity.kt`)
* Removed `import androidx.compose.material3.ExperimentalMaterial3ExpressiveApi`.
* Removed `@OptIn(ExperimentalMaterial3ExpressiveApi::class)` from `fun App(core: Core, resumed: Int)`.
* Verified that `FloatingActionButtonMenu` and `ToggleFloatingActionButton` compile natively against stable 1.5.0.

### 4. GitHub Actions Workflows (`.github/workflows/`)
* **`build-android.yml`**: Added `push: branches: ['**']`. Pushing commits automatically triggers the standard and full APK release builds.
* **`test.yml`**: Updated `push.branches` to `['**']` to run lint and debug variant assembly on every push.

---

## 4. Handoff & Maintenance Guide

For future maintainers working on Forkdesk Android:

1. **Local Build Command:**
   ```bash
   cd android
   ./gradlew assembleStandardDebug assembleFullDebug
   ```
2. **Upcoming Expressive Enhancements to Consider:**
   * **`HorizontalFloatingToolbar`:** In `BtPad.kt`, `Pointer.kt`, and `Remote.kt`, the static bottom navigation bar can be migrated to an expressive floating toolbar with scroll-to-hide behaviors.
   * **`SegmentedListItem`:** In `HistoryView.kt`, `Modules.kt`, and `General.kt`, list items can be wrapped in `ListItemDefaults.segmentedShapes(index, count)` to provide iOS/Material hybrid card grouping.
   * **`LinearWavyProgressIndicator`:** For active media and file transfer states in `Files.kt` and `Dav.kt`.

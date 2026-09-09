import SwiftUI

@main
struct VoiceApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @ObservedObject private var coordinator = Coordinator.shared
    // C7: a separate @ObservedObject on `sync` — a nested ObservableObject's own @Published changes
    // (unauthorized, userName) do not propagate through `coordinator`'s objectWillChange, so the menu
    // needs to observe it directly to redraw the badge/pickers.
    @ObservedObject private var sync = Coordinator.shared.sync
    @Environment(\.openWindow) private var openWindow
    @AppStorage(Preferences.Key.mode) private var mode = Preferences.mode
    @AppStorage(Preferences.Key.language) private var language = Preferences.language

    private var menuIcon: String {
        if sync.unauthorized { return "mic.badge.xmark" }
        return coordinator.hud == .listening ? "mic.fill" : "mic"
    }

    var body: some Scene {
        MenuBarExtra(Strings.appName, systemImage: menuIcon) {
            Text(coordinator.paused ? Strings.pause : Strings.idle)
            Text(coordinator.modelStatus)
            Divider()
            Picker(Strings.mode, selection: $mode) {
                Text(Strings.modeClean).tag("clean")
                Text(Strings.modeLiteral).tag("literal")
            }
            .onChange(of: mode) { _, newValue in Task { await coordinator.sync.push(mode: newValue) } }
            Picker(Strings.language, selection: $language) {
                Text(Strings.langAuto).tag("auto")
                Text(Strings.langPt).tag("pt")
                Text(Strings.langEn).tag("en")
            }
            .onChange(of: language) { _, newValue in Task { await coordinator.sync.push(language: newValue) } }
            Divider()
            Button(Strings.copyLast) {
                if let t = coordinator.lastText { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(t, forType: .string) }
            }.disabled(coordinator.lastText == nil)
            Button(coordinator.paused ? Strings.resume : Strings.pause) { coordinator.paused.toggle() }
            Divider()
            Button(Strings.setUpPermissions) { openWindow(id: "onboarding"); NSApp.activate(ignoringOtherApps: true) }
            SettingsLink { Text(Strings.settings) }
            Divider()
            Button(Strings.quit) { NSApplication.shared.terminate(nil) }.keyboardShortcut("q")
        }
        .menuBarExtraStyle(.menu)

        Window(Strings.onboardingTitle, id: "onboarding") { OnboardingView(coordinator: coordinator) }
            .windowResizability(.contentSize)

        Settings { Text("Settings arrive in Task 9").padding() }
    }
}

/// A2: owns app-launch sequencing (defaults → activation policy → coordinator start) and, on first
/// launch only, its own onboarding window — separate from the SwiftUI `Window(id: "onboarding")`
/// scene the menu item opens, since a window cannot be opened from here via `openWindow` (no view
/// context exists yet at `applicationDidFinishLaunching`).
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var onboardingWindow: NSWindow?

    func applicationDidFinishLaunching(_ notification: Notification) {
        Preferences.registerDefaults()
        NSApp.setActivationPolicy(.accessory)
        Coordinator.shared.start()
        if !Preferences.onboarded {
            showOnboarding()
        }
    }

    func showOnboarding() {
        let window: NSWindow
        if let existing = onboardingWindow {
            window = existing
        } else {
            window = NSWindow(contentViewController: NSHostingController(rootView: OnboardingView(coordinator: Coordinator.shared)))
            window.title = Strings.onboardingTitle
            window.styleMask = [.titled, .closable]
            window.isReleasedWhenClosed = false
            onboardingWindow = window
        }
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }
}

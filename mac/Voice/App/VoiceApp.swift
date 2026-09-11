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
            Button(Strings.insightsMenuItem) { openWindow(id: "insights"); NSApp.activate(ignoringOtherApps: true) }
            Button(Strings.setUpPermissions) { openWindow(id: "onboarding"); NSApp.activate(ignoringOtherApps: true) }
            SettingsLink { Text(Strings.settings) }
            Divider()
            Button(Strings.quit) { NSApplication.shared.terminate(nil) }.keyboardShortcut("q")
        }
        .menuBarExtraStyle(.menu)

        // Declared before onboarding so it is the app's main window. `defaultLaunchBehavior` is
        // explicit on both: now that Voice is `.regular`, a Window scene can present itself at
        // launch, and the one that does must be Insights and never the onboarding sheet.
        Window(Strings.insightsTitle, id: "insights") {
            InsightsView(model: Coordinator.shared.insights)
                .onAppear {
                    // The only place `openWindow` exists is inside a view. Hand it to the router so
                    // AppDelegate can reopen this window from a dock click, long after this view
                    // has gone away.
                    WindowRouter.open = { openWindow(id: $0) }
                }
        }
        .defaultSize(width: 900, height: 620)
        .windowResizability(.contentMinSize)
        .defaultLaunchBehavior(.presented)

        Window(Strings.onboardingTitle, id: "onboarding") { OnboardingView(coordinator: coordinator) }
            .windowResizability(.contentSize)
            .defaultLaunchBehavior(.suppressed)

        // D4: SettingsView reads Coordinator.shared directly rather than the @ObservedObject
        // instances above, since a Settings scene's content closure is built fresh each time the
        // window opens.
        Settings { SettingsView(coordinator: Coordinator.shared, sync: Coordinator.shared.sync) }
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
        // Rule 1: Voice is a regular app now — dock icon, ⌘-Tab, a real window. This must agree
        // with `LSUIElement: false` in project.yml, which AppKit reads before any of this runs.
        NSApp.setActivationPolicy(.regular)
        // Under XCTest this process is only a host for the test bundle, and starting the OS
        // adapters here makes the suite unrunnable: `AudioRecorder.prepare()` calls
        // `AVAudioEngine.inputNode`, which blocks the main thread on the microphone TCC gate. The
        // ad-hoc signature changes on every rebuild so that grant is gone every time, nobody can
        // click Allow in an unattended run, and the test runner times out before it ever connects
        // ("the test runner hung before establishing connection" — verified by stack sample, and
        // reproduced identically on the commit before this feature existed).
        //
        // This changes nothing at runtime: XCTestCase only exists in the process when the test
        // bundle has been injected. No test constructs Coordinator.shared — they all build their
        // own objects — so nothing is lost by not starting it.
        guard NSClassFromString("XCTestCase") == nil else { return }
        Coordinator.shared.start()
        if !Preferences.onboarded {
            showOnboarding()
        }
    }

    /// Closing the Insights window must not quit Voice (rule 4). Voice is a dictation service that
    /// happens to have a window; quitting on close would silently stop the hotkey working and the
    /// user would have no reason to connect the two events.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    /// Clicking the dock icon with no window open brings Insights back (rule 5). Without this the
    /// dock icon is inert once the window is closed, which reads as a broken app.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if !flag { WindowRouter.openInsights() }
        NSApp.activate(ignoringOtherApps: true)
        return true
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

/// Opens SwiftUI `Window` scenes from places that have no view context — `AppDelegate`, mainly.
///
/// `openWindow` only exists inside a view, so the Insights scene hands its own action over on
/// first appearance and this holds it for later. The action stays valid after the window is
/// closed, which is exactly the case that matters: the dock icon has to reopen a window that is
/// no longer there. The fallback covers the window between launch and that first appearance.
@MainActor
enum WindowRouter {
    static var open: ((String) -> Void)?

    static func openInsights() {
        if let open {
            open("insights")
        } else if let existing = NSApp.windows.first(where: { $0.identifier?.rawValue.contains("insights") == true }) {
            existing.makeKeyAndOrderFront(nil)
        }
    }
}

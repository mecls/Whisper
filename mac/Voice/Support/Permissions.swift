import AppKit
import ApplicationServices
import AVFoundation

enum Permissions {
    enum Pane { case inputMonitoring, accessibility, microphone, keyboard }

    static func inputMonitoringGranted() -> Bool { CGPreflightListenEventAccess() }
    static func requestInputMonitoring() { _ = CGRequestListenEventAccess() }

    static func accessibilityGranted(prompt: Bool) -> Bool {
        let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: prompt] as CFDictionary
        return AXIsProcessTrustedWithOptions(opts)
    }

    static func microphoneGranted() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return true
        case .notDetermined: return await AVCaptureDevice.requestAccess(for: .audio)
        default: return false
        }
    }

    /// macOS reacts to Fn itself unless "Press 🌐 key to" is "Do Nothing" (AppleFnUsageType = 0).
    static func fnUsageIsDoNothing() -> Bool {
        let v = CFPreferencesCopyAppValue("AppleFnUsageType" as CFString, "com.apple.HIToolbox" as CFString) as? Int
        return (v ?? 0) == 0
    }

    static func open(_ pane: Pane) {
        let url: String
        switch pane {
        case .inputMonitoring: url = "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent"
        case .accessibility: url = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"
        case .microphone: url = "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone"
        case .keyboard: url = "x-apple.systempreferences:com.apple.Keyboard-Settings.extension"
        }
        NSWorkspace.shared.open(URL(string: url)!)
    }

    // H5: shared by OnboardingView and the Settings Permissions tab (both via PermissionsView) —
    // relaunches the app (some grants only take effect after a fresh process) and quits this one.
    static func relaunch() {
        let url = Bundle.main.bundleURL
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        task.arguments = ["-n", url.path]
        try? task.run()
        NSApp.terminate(nil)
    }
}

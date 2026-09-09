import SwiftUI

struct OnboardingView: View {
    @ObservedObject var coordinator: Coordinator
    @State private var mic = false
    @State private var input = Permissions.inputMonitoringGranted()
    @State private var ax = Permissions.accessibilityGranted(prompt: false)
    @State private var fnOk = Permissions.fnUsageIsDoNothing()
    private let timer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Set up Voice").font(.title2.bold())
            row("Microphone", ok: mic) { Task { mic = await Permissions.microphoneGranted() } }
            row("Input Monitoring (to see the Fn key)", ok: input) { Permissions.requestInputMonitoring(); Permissions.open(.inputMonitoring) }
            row("Accessibility (to paste with ⌘V)", ok: ax) { _ = Permissions.accessibilityGranted(prompt: true); Permissions.open(.accessibility) }
            row("Keyboard › Press 🌐 key to → Do Nothing", ok: fnOk) { Permissions.open(.keyboard) }
            Divider()
            Text("Some grants only take effect after a relaunch.").font(.caption).foregroundStyle(.secondary)
            HStack {
                Button("Relaunch") { relaunch() }
                Spacer()
                Button("Done") { Preferences.onboarded = true; NSApp.keyWindow?.close() }
                    .disabled(!(mic && input && ax))
            }
        }
        .padding(24).frame(width: 440)
        .onReceive(timer) { _ in
            input = Permissions.inputMonitoringGranted()
            ax = Permissions.accessibilityGranted(prompt: false)
            fnOk = Permissions.fnUsageIsDoNothing()
        }
        .task { mic = await Permissions.microphoneGranted() }
    }

    private func row(_ title: String, ok: Bool, action: @escaping () -> Void) -> some View {
        HStack {
            Image(systemName: ok ? "checkmark.circle.fill" : "circle").foregroundStyle(ok ? .green : .secondary)
            Text(title)
            Spacer()
            if !ok { Button("Grant", action: action) }
        }
    }

    private func relaunch() {
        let url = Bundle.main.bundleURL
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        task.arguments = ["-n", url.path]
        try? task.run()
        NSApp.terminate(nil)
    }
}

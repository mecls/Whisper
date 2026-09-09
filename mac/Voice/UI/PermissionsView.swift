import SwiftUI

/// H5: the four live-updating permission rows plus "Relaunch", shared verbatim by `OnboardingView`
/// and the Settings window's Permissions tab — the one place this body exists.
struct PermissionsView: View {
    /// Called on every refresh (initial mic check, and every 1 s timer tick) with whether the three
    /// permissions the app actually needs to function (mic, input monitoring, accessibility) are all
    /// granted. `fnOk` is a nicety surfaced as its own row but never gates anything.
    var onStatusChange: ((_ granted: Bool) -> Void)?

    @State private var mic = false
    @State private var input = Permissions.inputMonitoringGranted()
    @State private var ax = Permissions.accessibilityGranted(prompt: false)
    @State private var fnOk = Permissions.fnUsageIsDoNothing()
    private let timer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    init(onStatusChange: ((_ granted: Bool) -> Void)? = nil) {
        self.onStatusChange = onStatusChange
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            row(Strings.permMicrophone, ok: mic) { Task { mic = await Permissions.microphoneGranted(); notify() } }
            row(Strings.permInputMonitoring, ok: input) { Permissions.requestInputMonitoring(); Permissions.open(.inputMonitoring) }
            row(Strings.permAccessibility, ok: ax) { _ = Permissions.accessibilityGranted(prompt: true); Permissions.open(.accessibility) }
            row(Strings.permFnKeyboard, ok: fnOk) { Permissions.open(.keyboard) }
            Divider()
            Button(Strings.relaunch) { Permissions.relaunch() }
        }
        .onReceive(timer) { _ in
            input = Permissions.inputMonitoringGranted()
            ax = Permissions.accessibilityGranted(prompt: false)
            fnOk = Permissions.fnUsageIsDoNothing()
            notify()
        }
        .task {
            mic = await Permissions.microphoneGranted()
            notify()
        }
    }

    private func notify() { onStatusChange?(mic && input && ax) }

    private func row(_ title: String, ok: Bool, action: @escaping () -> Void) -> some View {
        HStack {
            Image(systemName: ok ? "checkmark.circle.fill" : "circle").foregroundStyle(ok ? .green : .secondary)
            Text(title)
            Spacer()
            if !ok { Button(Strings.grant, action: action) }
        }
    }
}

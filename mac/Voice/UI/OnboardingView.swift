import SwiftUI

/// H5: thin wrapper around the shared `PermissionsView` — title/intro text plus the onboarding-only
/// "Done" gating, which needs to know whether every permission is granted without duplicating
/// `PermissionsView`'s body (hence the `onStatusChange` callback).
struct OnboardingView: View {
    @ObservedObject var coordinator: Coordinator
    @State private var allGranted = false

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(Strings.onboardingTitle).font(.title2.bold())
            PermissionsView { granted in allGranted = granted }
            Divider()
            Text(Strings.onboardingRelaunchNote).font(.caption).foregroundStyle(.secondary)
            HStack {
                Spacer()
                Button(Strings.onboardingDone) { Preferences.onboarded = true; NSApp.keyWindow?.close() }
                    .disabled(!allGranted)
            }
        }
        .padding(24).frame(width: 440)
    }
}

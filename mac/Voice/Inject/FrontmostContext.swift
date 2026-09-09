import AppKit

enum FrontmostContext {
    static func current() -> FrontmostApp? {
        guard let app = NSWorkspace.shared.frontmostApplication else { return nil }
        return FrontmostApp(bundleId: app.bundleIdentifier, name: app.localizedName)
    }
}

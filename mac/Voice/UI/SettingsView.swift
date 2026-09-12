import ServiceManagement
import SwiftUI

/// The five settings tabs, carrying no size of their own.
///
/// Two places show them, and they want opposite things. The ⌘, scene wants a fixed 520×380 window.
/// `MainWindow`'s detail pane wants them to fill whatever the window is, which is at least 720 wide
/// — a fixed frame there would strand the tabs at 520pt in the middle of an empty pane. Keeping the
/// frame out of here is what lets one view serve both; the caller supplies the size it needs.
struct SettingsTabs: View {
    @ObservedObject var coordinator: Coordinator
    @ObservedObject var sync: SyncService

    /// One column width for every pane, matching the ⌘, window so the two presentations agree.
    /// In that window it changes nothing — there is less width than this to give. In `MainWindow`'s
    /// much wider detail pane it is what stops a caption running the full width of the screen.
    static let contentWidth: CGFloat = 520

    var body: some View {
        TabView {
            GeneralTab(coordinator: coordinator, sync: sync).settingsPane()
                .tabItem { Label(Strings.settingsTabGeneral, systemImage: "gear") }
            ModelTab(coordinator: coordinator).settingsPane()
                .tabItem { Label(Strings.settingsTabModel, systemImage: "waveform") }
            ServerTab(sync: sync).settingsPane()
                .tabItem { Label(Strings.settingsTabServer, systemImage: "network") }
            DictionaryTab(coordinator: coordinator, sync: sync).settingsPane()
                .tabItem { Label(Strings.settingsTabDictionary, systemImage: "textformat.abc") }
            PermissionsTab().settingsPane()
                .tabItem { Label(Strings.settingsTabPermissions, systemImage: "lock.shield") }
        }
    }
}

private extension View {
    /// Puts every settings pane in the same box.
    ///
    /// The panes are `Form`s, which filled the ⌘, window almost exactly and do something else
    /// entirely in a detail pane several hundred points taller: offered more height than they
    /// need, they sit in the middle of it. Each tab then parks at a height set by how much
    /// content it happens to have, so switching from General to Model slides everything up or
    /// down and the window looks like it is rebuilding itself.
    ///
    /// Pinning each pane to the top of a fixed column is what makes switching tabs change the
    /// content and nothing else.
    ///
    /// The two frames do different jobs. The inner one sets the column and keeps the controls
    /// inside it left-aligned, so labels and checkboxes line up with each other. The outer one
    /// centres that column in the pane and holds it at the top — `.top` rather than `.topLeading`,
    /// which is the whole difference between a centred column and one stuck against the edge.
    func settingsPane() -> some View {
        frame(maxWidth: SettingsTabs.contentWidth, alignment: .leading)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }
}

/// Task 9 — the Settings window. D4: constructed as `SettingsView(coordinator: Coordinator.shared,
/// sync: Coordinator.shared.sync)` from `VoiceApp`'s `Settings` scene.
struct SettingsView: View {
    @ObservedObject var coordinator: Coordinator
    @ObservedObject var sync: SyncService

    var body: some View {
        SettingsTabs(coordinator: coordinator, sync: sync)
            .frame(width: 520, height: 380)
    }
}

/// A wrapping caption under a settings control.
///
/// The width is bounded on purpose, and every caption in this window must go through here.
/// `Text(...).fixedSize(horizontal: false, vertical: true)` — the obvious way to make a caption
/// wrap — does the exact opposite inside a `NavigationSplitView` detail pane. Asked for its ideal
/// size with no width proposed, the text reports a single unbroken line: 1109 points for the live
/// transcription note. The detail pane then asks the window for 1149 points next to a 184-point
/// sidebar, `NavigationSplitView` cannot satisfy that against a 900-point window, and it resolves
/// by rendering *both* columns empty — a blank window that survives switching tabs and only clears
/// on relaunch. Nothing errors, nothing hangs, and the view body evaluates normally, so there is
/// no thread to sample and no exception to catch.
///
/// `idealWidth` is what fixes it: it caps what the pane asks the window for, while `maxWidth`
/// still lets the caption use a wider window. `maxWidth` alone does not work — with no proposed
/// width the text is laid out as one line and then clipped to the frame, so it stops wrapping.
struct SettingsNote: View {
    /// Wide enough for a readable measure, far below the detail pane's budget. `SettingsViewTests`
    /// pins this: a caption whose ideal width climbs back toward the window width fails there
    /// rather than in a blank window.
    static let idealWidth: CGFloat = 420

    private let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(.caption)
            .foregroundStyle(.secondary)
            .frame(idealWidth: Self.idealWidth, maxWidth: .infinity, alignment: .leading)
    }
}

// MARK: - General

struct GeneralTab: View {
    @ObservedObject var coordinator: Coordinator
    @ObservedObject var sync: SyncService
    @AppStorage(Preferences.Key.showBar) private var showBar = true
    @AppStorage(Preferences.Key.liveTranscription) private var liveTranscription = false
    // D1: Preferences is a static namespace — views bind with @AppStorage, not @ObservedObject.
    @AppStorage(Preferences.Key.hotkey) private var hotkeyRaw = HotkeyChoice.fn.rawValue
    @AppStorage(Preferences.Key.sounds) private var sounds = true
    @AppStorage(Preferences.Key.showTextInHUD) private var showTextInHUD = true
    // Loaded in `.task`, never in the initialiser. `SMAppService.mainApp.status` is a synchronous
    // XPC call to the service-management daemon, and a @State default expression runs while the
    // view is being constructed — on the main thread, during the navigation transition that is
    // presenting it. That blocked the main thread hard enough to paint an empty window and hang the
    // app until it was relaunched, and it is slowest of all for an app running outside
    // /Applications, which is exactly how this one is built and run.
    @State private var launchAtLogin = false
    @State private var launchAtLoginError: String?

    private var hotkeyChoice: Binding<HotkeyChoice> {
        Binding(
            get: { HotkeyChoice(rawValue: hotkeyRaw) ?? .fn },
            set: { newValue in
                // D2: Coordinator.setHotkey stays as-is — HotkeyMonitor itself defers the swap if the
                // key is currently held.
                coordinator.setHotkey(newValue)
                Task { await sync.push(hotkey: newValue) }
            }
        )
    }

    var body: some View {
        Form {
            Picker(Strings.hotkeyPickerLabel, selection: hotkeyChoice) {
                ForEach(HotkeyChoice.allCases, id: \.self) { Text($0.label).tag($0) }
            }
            Toggle(Strings.playSounds, isOn: $sounds)
            Toggle(Strings.showTextInHUDToggle, isOn: $showTextInHUD)
            Toggle(Strings.showBar, isOn: $showBar)
                .onChange(of: showBar) { _, _ in coordinator.refreshBarVisibility() }
            Toggle(Strings.liveTranscription, isOn: $liveTranscription)
            SettingsNote(Strings.liveTranscriptionNote)
            Toggle(Strings.launchAtLogin, isOn: $launchAtLogin).onChange(of: launchAtLogin) { _, on in
                Task {
                    let failure: String? = await Task.detached(priority: .userInitiated) {
                        do {
                            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
                            return nil
                        } catch {
                            return Strings.launchAtLoginError(error.localizedDescription)
                        }
                    }.value
                    if let failure {
                        launchAtLogin = !on          // the switch must not claim a state it failed to reach
                        launchAtLoginError = failure
                    } else {
                        launchAtLoginError = nil
                    }
                }
            }
            if let launchAtLoginError {
                Text(launchAtLoginError).font(.caption).foregroundStyle(.red)
            }
        }
        .padding()
        .task {
            // Off the main actor as well as out of the initialiser: the daemon can take its time,
            // and the only cost of answering late is a toggle that settles a moment after the pane
            // appears.
            launchAtLogin = await Task.detached(priority: .userInitiated) {
                SMAppService.mainApp.status == .enabled
            }.value
        }
    }
}

// MARK: - Model

struct ModelTab: View {
    @ObservedObject var coordinator: Coordinator
    @AppStorage(Preferences.Key.modelId) private var modelId = "large-v3-v20240930_turbo_632MB"
    @AppStorage(Preferences.Key.language) private var language = "auto"
    private let manager = ModelManager()
    @State private var downloaded = false
    @State private var deleteError: String?

    private var activeLabel: String {
        ModelManager.available.first { $0.id == coordinator.activeModelId }?.label ?? coordinator.activeModelId
    }

    var body: some View {
        Form {
            Picker(Strings.modelPickerLabel, selection: $modelId) {
                ForEach(ModelManager.available, id: \.id) { m in Text(m.label).tag(m.id) }
            }
            .onChange(of: modelId) { _, newId in
                refreshDownloaded()
                // H2: picking an already-downloaded model swaps to it right away; picking one that
                // still needs fetching waits for an explicit "Download".
                if manager.isDownloaded(newId) { coordinator.reloadModel() }
            }
            // H3: the picker's selection and the model actually loaded can differ (e.g. right after
            // picking a not-yet-downloaded model) — this line disambiguates them.
            Text(Strings.activeModel(activeLabel))
            Text(coordinator.modelStatus)
            HStack {
                // D5: reloadModel() swaps the transcriber for the currently-picked modelId, resets
                // the progress HUD, and re-runs prepareModel() — this also drives a fresh download.
                Button(Strings.downloadModel) { coordinator.reloadModel() }
                    .disabled(downloaded)
                // H3: always enabled — the recovery path when prepareModel() failed (see the comment
                // on its catch) and the machine is stuck in .modelLoading with no other way out.
                Button(Strings.reloadModel) { coordinator.reloadModel() }
                Button(Strings.deleteModel) {
                    do {
                        try manager.delete(modelId)
                        deleteError = nil
                        refreshDownloaded()
                    } catch {
                        deleteError = Strings.modelDeleteError(error.localizedDescription)
                    }
                }
                // H3: never delete the model actually loaded into the transcriber.
                .disabled(modelId == coordinator.activeModelId)
            }
            if let deleteError {
                Text(deleteError).foregroundStyle(.red)
            }
            Picker(Strings.defaultLanguage, selection: $language) {
                Text(Strings.langAuto).tag("auto")
                Text(Strings.langPt).tag("pt")
                Text(Strings.langEn).tag("en")
            }
            .onChange(of: language) { _, newValue in Task { await coordinator.sync.push(language: newValue) } }
        }
        .padding()
        .onAppear { refreshDownloaded() }
        // modelStatus changes (e.g. "loading NN %" → "ready") whenever a download/load actually
        // completes — re-check isDownloaded then so Download/Delete stay in sync without polling.
        .onChange(of: coordinator.modelStatus) { _, _ in refreshDownloaded() }
    }

    private func refreshDownloaded() { downloaded = manager.isDownloaded(modelId) }
}

// MARK: - Server

struct ServerTab: View {
    @ObservedObject var sync: SyncService
    @AppStorage(Preferences.Key.serverURL) private var serverURL = "https://voice.miraside.co"
    @State private var token = ""

    var body: some View {
        Form {
            TextField(Strings.serverURLLabel, text: $serverURL)
            Text(Strings.serverURLChangeNote)
                .font(.caption)
                .foregroundStyle(.secondary)
            // D6: the only view in the app that may call Keychain.set/delete, and only from these
            // button actions — never on launch, never from a test.
            SecureField(Strings.apiTokenLabel, text: $token)
            HStack {
                Button(Strings.saveToken) {
                    Keychain.set(token: token, for: Preferences.serverURL)
                    // H4: clear the field before starting the (async) sync — the token is now only
                    // in the Keychain, never lingering on screen.
                    token = ""
                    Task { await sync.sync() }
                }
                .disabled(token.isEmpty)
                Button(Strings.testConnection) { Task { await sync.sync() } }
                // D9: SyncService.signOut() owns the Keychain.delete call plus the unauthorized/userName
                // updates (both are private(set) on SyncService).
                Button(Strings.signOut) { sync.signOut(); token = "" }
            }
            if sync.unauthorized {
                Text(Strings.tokenInvalid)
            } else if let userName = sync.userName {
                Text(Strings.connectedAs(userName))
            }
        }
        .padding()
    }
}

// MARK: - Dictionary

struct DictionaryTab: View {
    @ObservedObject var coordinator: Coordinator
    @ObservedObject var sync: SyncService
    @State private var entries: [DictionaryEntry] = []
    @State private var newTerm = ""
    @State private var newReplacement = ""
    @State private var teamWide = false
    @State private var errorMessage: String?

    var body: some View {
        Form {
            Section {
                if entries.isEmpty {
                    Text(Strings.dictionaryEmpty).foregroundStyle(.secondary)
                } else {
                    ForEach(entries) { entry in
                        HStack {
                            Text(entry.replacement.map { Strings.dictionaryReplacement(entry.term, $0) } ?? entry.term)
                            Spacer()
                            Button { Task { await delete(entry) } } label: { Image(systemName: "trash") }
                                .buttonStyle(.borderless)
                        }
                    }
                }
            }
            Section {
                TextField(Strings.dictionaryTermPlaceholder, text: $newTerm)
                TextField(Strings.dictionaryReplacementPlaceholder, text: $newReplacement)
                Toggle(Strings.dictionaryTeamWide, isOn: $teamWide)
                Button(Strings.dictionaryAdd) { Task { await add() } }
                    .disabled(newTerm.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            if let errorMessage {
                Text(errorMessage).foregroundStyle(.red)
            }
        }
        .padding()
        // D3: GET /v1/dictionary via listTerms() (not api.me(), whose dictionary entries lack `id`).
        .task { await load() }
    }

    private func load() async {
        do {
            entries = try await coordinator.api.listTerms()
            errorMessage = nil
        } catch {
            errorMessage = Strings.dictionaryLoadError
        }
    }

    private func add() async {
        do {
            _ = try await coordinator.api.addTerm(newTerm, replacement: newReplacement.isEmpty ? nil : newReplacement, teamWide: teamWide)
            newTerm = ""
            newReplacement = ""
            teamWide = false
            errorMessage = nil
            await sync.sync()   // refreshes DictionaryCache.shared so the next dictation sees the new term
            await load()
        } catch {
            errorMessage = Strings.dictionaryAddError
        }
    }

    private func delete(_ entry: DictionaryEntry) async {
        do {
            try await coordinator.api.deleteTerm(id: entry.id)
            errorMessage = nil
            await sync.sync()
            await load()
        } catch {
            errorMessage = Strings.dictionaryDeleteError
        }
    }
}

// MARK: - Permissions

// H5: the rows themselves now live in the shared `PermissionsView` (also used by `OnboardingView`)
// so there is exactly one copy of that body in the app.
struct PermissionsTab: View {
    var body: some View {
        Form {
            PermissionsView()
        }
        .padding()
    }
}

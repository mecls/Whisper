namespace Spit.Core;

/// Every user-facing string, as mac/Voice/UI/Strings.swift. Names match the Mac's so a message can be
/// traced across clients; values differ only where Windows has different keys, settings paths or
/// limitations (prd-spit-mac-windows.md rules 27, 30, 33, 39, 40, 43).
public static class Strings
{
    public const string AppName = "Spit";
    public const string Idle = "Ready — hold Right Ctrl to dictate";
    public static string IdleFor(string keyLabel) => $"Ready — hold {keyLabel} to dictate";
    public const string Listening = "Listening…";
    public const string Transcribing = "Transcribing…";
    public const string Cleaning = "Cleaning…";
    public const string Done = "Done";
    public const string ViaCleaned = "cleaned";
    public const string ViaSkipped = "already clean";
    public const string NothingHeard = "Nothing heard";
    public const string Cancelled = "Cancelled";
    public const string PastedRaw = "Pasted raw — server slow or offline";
    public const string TokenInvalid = "Token invalid — open Settings";

    /// The Mac's secure-input diversion. On Windows the reducer's clipboard-only paths are the target
    /// being Spit itself (rule 34); the admin-window and clipboard-busy cases have their own strings.
    public const string SecureField = "Spit is focused — text copied to clipboard instead";
    public const string AdminWindow = "Admin window — text copied to clipboard";
    public const string ClipboardBusy = "Couldn't use the clipboard — use Copy last dictation";
    public const string NoMicrophone = "No microphone available — check Settings › System › Sound › Input";
    public const string MicrophoneBlocked = "Microphone blocked — Settings › Privacy & security › Microphone";
    public const string OpenMicrophoneSettings = "Open microphone settings";

    // Hands-free dictation
    public const string TapTooShort = "Too short — tap twice to dictate hands-free";
    public const string Latched = "hands-free";
    public const string LatchCapReached = "Reached the 90 s limit — transcribing";
    public const string ShowBar = "Show bar";
    public const string LiveTranscription = "Live transcription (experimental)";
    public const string LiveTranscriptionNote = "Transcribes while you speak, so the text is ready the moment you stop — removing the transcription wait from every dictation. Chunked transcription can be slightly less accurate than a single pass; turn it off if you notice mistakes.";
    public const string ModelLoading = "Model loading";
    public const string ModelNotDownloaded = "Model not downloaded";
    public static string ModelDownloading(int percent) => $"Downloading model — {percent}%";
    public const string ModelChecksumMismatch = "Model download failed — checksum mismatch";
    public static string ModelDownloadFailed(string message) => $"Model download failed: {message}";
    public const string CopyLast = "Copy last dictation";
    public const string Pause = "Pause dictation";
    public const string Resume = "Resume dictation";
    public const string Settings = "Settings…";
    public const string SettingsNav = "Settings";
    public const string Quit = "Quit Spit";
    public const string Mode = "Mode";
    public const string Language = "Language";
    public const string ModeClean = "Clean";
    public const string ModeLiteral = "Literal";
    public const string LangAuto = "Auto";
    public const string LangPt = "Português";
    public const string LangEn = "English";

    /// The Mac's "Set up permissions…"; Windows has no permission grants, only the Set-up rows (rule 40).
    public const string SetUpPermissions = "Set up…";

    // Insights window
    public const string InsightsTitle = "Insights";
    public const string InsightsMenuItem = "Insights…";
    public const string InsightsRefresh = "Refresh";
    public const string InsightsEmpty = "Dictations will appear here once you start using Spit.";
    public const string CardTotalWords = "Total words";
    public const string CardTotalWordsCaption = "total words";
    public const string CardWpm = "Words per minute";
    public const string CardWpmCaption = "words per minute";
    public const string CardStreak = "Streak";
    public const string CardApps = "Apps";
    public const string NoValue = "—";
    public static string DayStreak(int n) => n == 1 ? "1 day streak" : $"{n} day streak";
    public static string LongestStreak(int n) => $"longest: {n} days";
    public static string LastUpdated(string relative) => $"Last updated {relative} · couldn't reach the server";
    public const string LastUpdatedNever = "Couldn't reach the server";
    public const string TokenInvalidInsights = "Token invalid — open Settings › Server";
    public const string TimeZoneUnknown = "Couldn't determine your time zone";
    public const string ModelReady = "Model: ready";
    public static string ModelError(string message) => $"Model error: {message}";
    public const string ModelLabelTurbo = "Large v3 Turbo (compressed, 574 MB) — recommended";
    public const string ModelLabelLarge = "Large v3 Turbo (full, 1.6 GB) — maximum accuracy";

    // Set-up (replaces the Mac's onboarding permissions, rule 40)
    public const string OnboardingTitle = "Set up Spit";
    public const string SetupMicrophone = "Microphone";
    public const string SetupMicrophoneGranted = "Microphone: available";
    public const string SetupMicrophoneBlocked = "Microphone: blocked";
    public static string SetupHotkey(string keyLabel) => $"Hold {keyLabel} now";
    public static string SetupHotkeySeen(string keyLabel) => $"{keyLabel} works";
    public const string SetupToken = "Token";
    public const string SetupTokenMissing = "Paste your token in Settings › Server";
    public const string SetupTokenChecking = "Checking…";
    public const string OpenServerSettings = "Open Settings › Server";
    public const string OnboardingDone = "Done";

    public const string HotkeyLabelRightCtrl = "Right Ctrl";
    public const string HotkeyLabelRightAlt = "Right Alt";

    // Settings window
    public const string SettingsTabGeneral = "General";
    public const string SettingsTabModel = "Model";
    public const string SettingsTabServer = "Server";
    public const string SettingsTabDictionary = "Dictionary";

    public const string HotkeyPickerLabel = "Dictation key";
    public const string PlaySounds = "Play sounds";
    public const string ShowTextInHUDToggle = "Show text in the bar after pasting";
    public const string LaunchAtLogin = "Launch at login";
    public static string LaunchAtLoginError(string message) => $"Couldn't change Launch at login: {message}";

    public const string ModelPickerLabel = "Model";
    public const string DownloadModel = "Download";
    public const string DeleteModel = "Delete";
    public const string ReloadModel = "Reload model";
    public static string ActiveModel(string label) => $"Active: {label}";
    public static string ModelDeleteError(string message) => $"Couldn't delete the model: {message}";
    public const string DefaultLanguage = "Default language";

    public const string ServerURLLabel = "Server URL";
    public const string ServerURLChangeNote = "Changing the server URL requires relaunching Spit.";
    public const string ApiTokenLabel = "API token";
    public const string SaveToken = "Save token";
    public const string TestConnection = "Test connection";
    public const string SignOut = "Sign out";
    public const string NotConnected = "Not connected";
    public static string ConnectedAs(string name) => $"Connected as {name}";
    public static string SaveTokenError(string message) => $"Couldn't save the token: {message}";

    public const string DictionaryTermPlaceholder = "Term";
    public const string DictionaryReplacementPlaceholder = "Replacement (optional)";
    public const string DictionaryTeamWide = "Team-wide";
    public const string DictionaryAdd = "Add";
    public const string DictionaryEmpty = "No dictionary terms yet";
    public const string DictionaryLoadError = "Couldn't load the dictionary";
    public const string DictionaryAddError = "Couldn't add the term";
    public const string DictionaryDeleteError = "Couldn't delete the term";
    public static string DictionaryReplacement(string term, string replacement) => $"{term} → {replacement}";
}

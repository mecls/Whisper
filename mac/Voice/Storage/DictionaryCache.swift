import Foundation

/// Offline cache of the server's custom-vocabulary dictionary, handed to the transcriber as a hint.
/// Minimal now; Task 8 fills in fetch/persist.
final class DictionaryCache {
    static let shared = DictionaryCache()
    private(set) var terms: [String] = []
    func update(_ terms: [String]) { self.terms = terms }
}

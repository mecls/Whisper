import Foundation

protocol Refiner {
    func refine(_ d: Dictation, mode: String) async -> RefineResult
    func reportInjected(clientId: UUID, injected: Injected) async
}

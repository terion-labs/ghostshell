import Darwin
import Foundation

/// Helpers are not daemons. An app crash must not leave a usable route behind.
final class OwnerLifetime {
  private let timer: DispatchSourceTimer

  init(onLoss: @escaping @Sendable () -> Void) {
    let owner = getppid()
    timer = DispatchSource.makeTimerSource(queue: .global())
    timer.schedule(deadline: .now(), repeating: .milliseconds(250))
    timer.setEventHandler {
      if owner <= 1 || getppid() != owner { onLoss() }
    }
    timer.resume()
  }

  deinit { timer.cancel() }
}

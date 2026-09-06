import Containerization
import Foundation

/// Demand-driven SDK stream. At most two 32 KiB chunks are retained; the
/// control reader suspends when the guest is not consuming stdin.
actor ProcessInput: ReaderStream {
  private var buffered: Data?
  private var waitingReader: CheckedContinuation<Data?, Never>?
  private var waitingWriter: (Data, CheckedContinuation<Void, any Error>)?
  private var finished = false

  nonisolated func stream() -> AsyncStream<Data> {
    AsyncStream(unfolding: { await self.next() }, onCancel: { Task { await self.abort() } })
  }

  func write(_ data: Data) async throws {
    guard !finished else { throw RuntimeFailure("Stdin is closed.") }
    if let reader = waitingReader {
      waitingReader = nil
      reader.resume(returning: data)
      return
    }
    guard buffered != nil else {
      buffered = data
      return
    }
    try await withCheckedThrowingContinuation { continuation in
      waitingWriter = (data, continuation)
    }
  }

  private func next() async -> Data? {
    if let data = buffered {
      buffered = nil
      if let writer = waitingWriter {
        waitingWriter = nil
        buffered = writer.0
        writer.1.resume()
      }
      return data
    }
    guard !finished else { return nil }
    return await withCheckedContinuation { waitingReader = $0 }
  }

  func finish() {
    finished = true
    if buffered == nil {
      waitingReader?.resume(returning: nil)
      waitingReader = nil
    }
  }

  func abort() {
    buffered = nil
    waitingWriter?.1.resume(throwing: RuntimeFailure("Guest stdin closed."))
    waitingWriter = nil
    finish()
  }
}

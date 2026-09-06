import Darwin
import Foundation

/// Drains the gateway's private stderr without retaining or forwarding its raw
/// contents. Only these closed reason codes may cross the control connection.
final class NetworkExitDiagnostics: @unchecked Sendable {
  private let lock = NSLock()
  private let completed = DispatchGroup()
  private var stopping = false
  private var reason = "unknown"

  init(input: FileHandle) {
    completed.enter()
    DispatchQueue.global().async {
      defer {
        try? input.close()
        self.completed.leave()
      }
      var bytes = [UInt8](repeating: 0, count: 4096)
      var line = Data()
      var stopDeadline: UInt64?
      while true {
        if self.lock.withLock({ self.stopping }), stopDeadline == nil {
          // A descendant retaining stderr must not delay reconnect indefinitely.
          stopDeadline = DispatchTime.now().uptimeNanoseconds + 250_000_000
        }
        if let stopDeadline, DispatchTime.now().uptimeNanoseconds >= stopDeadline { break }
        var descriptor = pollfd(fd: input.fileDescriptor, events: Int16(POLLIN), revents: 0)
        let readable = poll(&descriptor, 1, stopDeadline == nil ? 100 : 0)
        if readable < 0 && errno == EINTR { continue }
        if readable == 0 && stopDeadline == nil { continue }
        guard readable > 0 else { break }
        let count = Darwin.read(input.fileDescriptor, &bytes, bytes.count)
        if count < 0 && errno == EINTR { continue }
        guard count > 0 else { break }
        for byte in bytes.prefix(count) {
          if byte == 10 {
            self.record(line)
            line.removeAll(keepingCapacity: true)
          } else if line.count < 4096 {
            line.append(byte)
          }
        }
      }
      self.record(line)
    }
  }

  func finish() -> String {
    lock.withLock { stopping = true }
    _ = completed.wait(timeout: .now() + 1)
    return lock.withLock { reason }
  }

  private func record(_ line: Data) {
    let classified = Self.classify(String(decoding: line, as: UTF8.self))
    if classified != "unknown" { lock.withLock { reason = classified } }
  }

  static func classify(_ line: String) -> String {
    if line.hasPrefix("panic:") || line.hasPrefix("fatal error:") { return "runtime-panic" }
    guard line.hasPrefix("workspace network gateway: ") else { return "unknown" }
    if line.hasSuffix("packet channel authentication failed") { return "protocol-authentication" }
    if line.hasSuffix("packet channel sequence is invalid") { return "protocol-sequence" }
    if line.hasSuffix("packet channel frame is malformed")
      || line.hasSuffix("packet channel frame is too large")
      || line.hasSuffix("packet channel version is unsupported")
    {
      return "protocol-malformed"
    }
    let message = line.dropFirst("workspace network gateway: ".count)
    if message.hasPrefix("read workspace NIC:") || message.hasPrefix("write workspace NIC:") {
      return message.hasSuffix("no buffer space available")
        ? "workspace-nic-congestion" : "workspace-nic-failed"
    }
    if message.hasPrefix("establish provider channel:") { return "provider-handshake-failed" }
    if message.hasPrefix("receive provider packet:") || message.hasPrefix("send provider packet:") {
      if message.hasSuffix(": EOF") || message.hasSuffix(": unexpected EOF")
        || message.hasSuffix(": broken pipe") || message.hasSuffix(": connection reset by peer")
      {
        return "provider-channel-closed"
      }
      return "provider-channel-failed"
    }
    return "unknown"
  }
}

struct NetworkExit: Sendable {
  let code: Int32
  let reason: String

  var diagnostic: Data {
    Data("GHOSTSHELL_GATEWAY_EXIT_REASON=\(reason)\n".utf8)
  }
}

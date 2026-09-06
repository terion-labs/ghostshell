import Darwin
import Foundation

/// One reader and serialized writers per channel. Closing unblocks the reader.
final class UnixChannel: @unchecked Sendable {
  let descriptor: Int32
  private let writeLock = NSLock()
  private let readLock = NSLock()
  private var pending = Data()
  private let ownership = NSLock()
  private var closed = false
  static let maximumRecordBytes = 1_048_576

  init(_ descriptor: Int32) {
    self.descriptor = descriptor
    var enabled: Int32 = 1
    _ = setsockopt(
      descriptor, SOL_SOCKET, SO_NOSIGPIPE, &enabled, socklen_t(MemoryLayout<Int32>.size))
    _ = fcntl(descriptor, F_SETFD, FD_CLOEXEC)
  }
  deinit { close() }

  static func validatePath(_ path: String) throws {
    guard path.hasPrefix("/"), !path.contains("\0"), path.utf8.count < 104 else {
      throw RuntimeFailure("Control socket path must be absolute and shorter than 104 bytes.")
    }
  }

  static func address(_ path: String) throws -> sockaddr_un {
    try validatePath(path)
    var address = sockaddr_un()
    address.sun_family = sa_family_t(AF_UNIX)
    address.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
    withUnsafeMutableBytes(of: &address.sun_path) { target in
      target.copyBytes(from: Array(path.utf8) + [0])
    }
    return address
  }

  static func connect(path: String) throws -> UnixChannel {
    var address = try address(path)
    let fd = socket(AF_UNIX, SOCK_STREAM, 0)
    guard fd >= 0 else { throw POSIXError(.EIO) }
    let result = withUnsafePointer(to: &address) {
      $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
        Darwin.connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
      }
    }
    guard result == 0 else {
      Darwin.close(fd)
      throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
    }
    return UnixChannel(fd)
  }

  static func listen(path: String) throws -> Int32 {
    var address = try address(path)
    let parent = URL(fileURLWithPath: path).deletingLastPathComponent().path
    var status = stat()
    guard lstat(parent, &status) == 0, status.st_uid == getuid(),
      status.st_mode & S_IFMT == S_IFDIR, status.st_mode & 0o077 == 0
    else {
      throw RuntimeFailure("Control socket requires a private owner-only parent directory.")
    }
    let fd = socket(AF_UNIX, SOCK_STREAM, 0)
    guard fd >= 0 else { throw POSIXError(.EIO) }
    _ = fcntl(fd, F_SETFD, FD_CLOEXEC)
    let result = withUnsafePointer(to: &address) {
      $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
        bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
      }
    }
    // Never unlink another runtime's control socket. Its owner must stop it.
    guard result == 0 else {
      Darwin.close(fd)
      throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
    }
    guard chmod(path, 0o600) == 0, Darwin.listen(fd, 32) == 0 else {
      Darwin.close(fd)
      unlink(path)
      throw POSIXError(.EIO)
    }
    return fd
  }

  func close() {
    let shouldClose = ownership.withLock {
      guard !closed else { return false }
      closed = true
      _ = shutdown(descriptor, SHUT_RDWR)
      return true
    }
    guard shouldClose else { return }
    // shutdown unblocks I/O before joining it; don't let a reused descriptor
    // receive a delayed read/write from the old channel.
    readLock.withLock {
      writeLock.withLock { _ = Darwin.close(descriptor) }
    }
  }

  func send(_ record: WireRecord) throws {
    let bytes = try JSONEncoder().encode(record) + Data([10])
    guard bytes.count <= Self.maximumRecordBytes else {
      throw RuntimeFailure("Control record is too large.")
    }
    try writeLock.withLock {
      guard !ownership.withLock({ closed }) else { throw POSIXError(.EPIPE) }
      try bytes.withUnsafeBytes { pointer in
        var offset = 0
        while offset < bytes.count {
          let count = Darwin.write(
            descriptor, pointer.baseAddress!.advanced(by: offset), bytes.count - offset)
          if count < 0 && errno == EINTR { continue }
          guard count > 0 else { throw POSIXError(.EPIPE) }
          offset += count
        }
      }
    }
  }

  func receive() throws -> WireRecord? {
    try readLock.withLock {
      guard !ownership.withLock({ closed }) else { return nil }
      return try receiveLocked()
    }
  }

  /// Blocking socket reads run on Dispatch workers, never Swift's bounded
  /// cooperative executor. Idle terminals must not starve VM RPC continuations.
  func receiveAsync() async throws -> WireRecord? {
    try await withCheckedThrowingContinuation { continuation in
      DispatchQueue.global().async {
        do { continuation.resume(returning: try self.receive()) } catch {
          continuation.resume(throwing: error)
        }
      }
    }
  }

  private func receiveLocked() throws -> WireRecord? {
    while true {
      if let newline = pending.firstIndex(of: 10) {
        let record = pending[..<newline]
        pending.removeSubrange(...newline)
        return try JSONDecoder().decode(WireRecord.self, from: record)
      }
      guard pending.count < Self.maximumRecordBytes else {
        throw RuntimeFailure("Control record is too large.")
      }
      var bytes = [UInt8](repeating: 0, count: min(16384, Self.maximumRecordBytes - pending.count))
      let count = Darwin.read(descriptor, &bytes, bytes.count)
      if count < 0 && errno == EINTR { continue }
      guard count >= 0 else { throw POSIXError(.EIO) }
      if count == 0 {
        guard pending.isEmpty else { throw RuntimeFailure("Truncated control record.") }
        return nil
      }
      pending.append(contentsOf: bytes.prefix(count))
    }
  }
}

import Darwin
import Foundation

/// Owns a child without inheriting unrelated VM/control descriptors.
final class NetworkChild: @unchecked Sendable {
  private let lock = NSLock()
  private var pid: pid_t
  private let socketPath: String
  private var socketIdentity: (device: dev_t, inode: ino_t)?
  private let diagnostics: NetworkExitDiagnostics
  let output: FileHandle
  var processIdentifier: pid_t { lock.withLock { pid } }

  init(executable: String, socketPath: String, nic: WorkspaceNIC, key: Data) throws {
    guard key.count == 32 else {
      throw RuntimeFailure("Network lease key must contain exactly 32 bytes.")
    }
    try UnixChannel.validatePath(socketPath)
    self.socketPath = socketPath
    var existing = stat()
    guard lstat(socketPath, &existing) != 0, errno == ENOENT else {
      throw RuntimeFailure("Refusing to replace an existing packet socket.")
    }
    let input = Pipe()
    let outputPipe = Pipe()
    let errorPipe = Pipe()
    var actions: posix_spawn_file_actions_t?
    var attributes: posix_spawnattr_t?
    posix_spawn_file_actions_init(&actions)
    posix_spawnattr_init(&attributes)
    defer {
      posix_spawn_file_actions_destroy(&actions)
      posix_spawnattr_destroy(&attributes)
    }
    posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_CLOEXEC_DEFAULT))
    posix_spawn_file_actions_adddup2(
      &actions, input.fileHandleForReading.fileDescriptor, STDIN_FILENO)
    posix_spawn_file_actions_adddup2(
      &actions, outputPipe.fileHandleForWriting.fileDescriptor, STDOUT_FILENO)
    posix_spawn_file_actions_adddup2(
      &actions, errorPipe.fileHandleForWriting.fileDescriptor, STDERR_FILENO)
    posix_spawn_file_actions_adddup2(&actions, nic.hostHandle.fileDescriptor, 3)
    let arguments: [UnsafeMutablePointer<CChar>?] =
      [executable, "ethernet", "--socket", socketPath].map { value in
        value.withCString { strdup($0) }
      } + [nil]
    let environment = [strdup("PATH=/usr/bin:/bin"), nil]
    defer { for pointer in arguments + environment { free(pointer) } }
    var child: pid_t = 0
    let result = arguments.withUnsafeBufferPointer { argv in
      environment.withUnsafeBufferPointer { env in
        posix_spawn(&child, executable, &actions, &attributes, argv.baseAddress!, env.baseAddress!)
      }
    }
    guard result == 0 else { throw POSIXError(POSIXErrorCode(rawValue: result) ?? .EIO) }
    pid = child
    output = outputPipe.fileHandleForReading
    diagnostics = NetworkExitDiagnostics(input: errorPipe.fileHandleForReading)
    try input.fileHandleForReading.close()
    try outputPipe.fileHandleForWriting.close()
    try errorPipe.fileHandleForWriting.close()
    do {
      try input.fileHandleForWriting.write(contentsOf: key)
      try input.fileHandleForWriting.close()
      let deadline = DispatchTime.now().uptimeNanoseconds + 15_000_000_000
      var ready = Data()
      while ready.count < 32 {
        let now = DispatchTime.now().uptimeNanoseconds
        guard now < deadline else {
          throw RuntimeFailure("Host Ethernet gateway readiness timed out.")
        }
        var poller = pollfd(fd: output.fileDescriptor, events: Int16(POLLIN), revents: 0)
        let remaining = Int32(max(1, (deadline - now) / 1_000_000))
        let readable = poll(&poller, 1, remaining)
        if readable < 0 && errno == EINTR { continue }
        guard readable > 0 else {
          throw RuntimeFailure("Host Ethernet gateway readiness timed out.")
        }
        guard let byte = try output.read(upToCount: 1), !byte.isEmpty else { break }
        ready.append(byte)
        if byte[0] == 10 { break }
      }
      guard ready == Data("READY v1\n".utf8) else {
        throw RuntimeFailure("Host Ethernet gateway failed readiness.")
      }
      recordSocketIdentity()
      guard socketIdentity != nil else {
        throw RuntimeFailure("Host Ethernet gateway packet socket is unavailable.")
      }
    } catch {
      recordSocketIdentity()
      terminate()
      _ = wait()
      _ = diagnostics.finish()
      throw error
    }
  }

  func terminate() {
    lock.withLock { if pid > 0 { _ = kill(pid, SIGTERM) } }
    DispatchQueue.global().asyncAfter(deadline: .now() + 1) { [weak self] in
      guard let self else { return }
      self.lock.withLock { if self.pid > 0 { _ = kill(self.pid, SIGKILL) } }
    }
  }

  private func recordSocketIdentity() {
    var info = stat()
    if lstat(socketPath, &info) == 0, info.st_mode & S_IFMT == S_IFSOCK, info.st_uid == getuid() {
      socketIdentity = (info.st_dev, info.st_ino)
    }
  }

  private func removeOwnedSocket() {
    guard let identity = socketIdentity else { return }
    var info = stat()
    if lstat(socketPath, &info) == 0, info.st_dev == identity.device, info.st_ino == identity.inode
    {
      if unlink(socketPath) != 0 {
        FileHandle.standardError.write(
          Data("workspace-runtime: packet socket cleanup failed.\n".utf8))
      }
    }
  }

  func wait() -> Int32 {
    let child = lock.withLock { pid }
    guard child > 0 else { return 0 }
    // Observe without reaping first. The PID cannot be reused between wait
    // completion and a concurrent terminate; reaping shares the same lock.
    var information = siginfo_t()
    while waitid(P_PID, id_t(child), &information, WEXITED | WNOWAIT) < 0 && errno == EINTR {}
    return lock.withLock {
      var status: Int32 = 0
      while waitpid(child, &status, 0) < 0 && errno == EINTR {}
      pid = 0
      removeOwnedSocket()
      return status & 0x7f == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f)
    }
  }

  func waitAsync() async -> NetworkExit {
    await withCheckedContinuation { continuation in
      DispatchQueue.global().async {
        let code = self.wait()
        continuation.resume(returning: NetworkExit(code: code, reason: self.diagnostics.finish()))
      }
    }
  }
}

actor NetworkLease {
  private let nic: WorkspaceNIC
  private let executable: String
  private var active: (UUID, NetworkChild, Task<NetworkExit, Never>)?
  private var generation = UUID()

  init(nic: WorkspaceNIC, executable: String) {
    self.nic = nic
    self.executable = executable
  }

  func start(socketPath: String, key: Data) async throws -> UUID {
    let id = UUID()
    generation = id
    let previous = active
    active = nil
    if let previous {
      previous.1.terminate()
      _ = await previous.2.value
    }
    // Actors may accept another request while awaiting the old child.
    guard generation == id else { throw RuntimeFailure("Network lease was superseded.") }
    nic.drain()
    let child = try NetworkChild(executable: executable, socketPath: socketPath, nic: nic, key: key)
    active = (id, child, Task { await child.waitAsync() })
    return id
  }

  func wait(id: UUID) async -> NetworkExit {
    guard let current = active, current.0 == id else {
      return NetworkExit(code: 0, reason: "unknown")
    }
    return await current.2.value
  }

  func stop(id: UUID? = nil) async {
    if id == nil { generation = UUID() }
    guard let current = active, id == nil || current.0 == id else { return }
    active = nil
    current.1.terminate()
    _ = await current.2.value
    nic.drain()
  }
}

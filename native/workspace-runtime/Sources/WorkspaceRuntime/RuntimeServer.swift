import Darwin
import Foundation
import Synchronization

final class RuntimeServer: Sendable {
  private let vm: WorkspaceVM
  private let listener: Int32
  private let stopped = Mutex(false)
  private let stopCompletion = Mutex<Task<Bool, Never>?>(nil)
  private let clients = DispatchGroup()

  init(configuration: ServeConfiguration) throws {
    vm = try WorkspaceVM(configuration: configuration)
    listener = try UnixChannel.listen(path: configuration.controlSocketPath)
  }

  func run() async throws {
    defer { unlink(vm.configuration.controlSocketPath) }
    do { try await vm.start() } catch {
      _ = Darwin.close(listener)
      throw error
    }
    if stopped.withLock({ $0 }) {
      // Owner loss may race SDK creation. Complete teardown after creation even
      // if the earlier stop request arrived before the VM existed.
      try await vm.stop()
      return
    }
    print("READY v1")
    fflush(stdout)
    Task {
      do { _ = try await self.vm.container.wait() } catch {
        FileHandle.standardError.write(
          Data("workspace-runtime: VM initial process wait failed.\n".utf8))
      }
      await self.stop()
    }
    while !stopped.withLock({ $0 }) {
      let socket = await withCheckedContinuation { continuation in
        DispatchQueue.global().async {
          continuation.resume(returning: accept(self.listener, nil, nil))
        }
      }
      guard socket >= 0 else {
        if stopped.withLock({ $0 }) { break }
        if errno == EINTR { continue }
        await stop()
        throw RuntimeFailure("Runtime control listener failed.")
      }
      let channel = UnixChannel(socket)
      clients.enter()
      Task.detached {
        defer { self.clients.leave() }
        await self.handle(channel)
      }
    }
    let cleanStop = await stop()
    // Let the stop caller receive its acknowledgement before main exits.
    await withCheckedContinuation { continuation in
      DispatchQueue.global().async {
        self.waitForClientAcknowledgements()
        continuation.resume()
      }
    }
    guard cleanStop else { throw RuntimeFailure("Workspace VM could not be stopped cleanly.") }
  }

  private func waitForClientAcknowledgements() {
    _ = clients.wait(timeout: .now() + 5)
  }

  @discardableResult
  func stop() async -> Bool {
    let completion = stopCompletion.withLock { existing in
      if let existing { return existing }
      stopped.withLock { $0 = true }
      let task = Task {
        _ = shutdown(self.listener, SHUT_RDWR)
        _ = Darwin.close(self.listener)
        do {
          try await self.vm.stop()
          return true
        } catch {
          FileHandle.standardError.write(Data("workspace-runtime: VM stop failed.\n".utf8))
          return false
        }
      }
      existing = task
      return task
    }
    return await completion.value
  }

  private func handle(_ channel: UnixChannel) async {
    defer { channel.close() }
    do {
      var uid: uid_t = 0
      var gid: gid_t = 0
      guard getpeereid(channel.descriptor, &uid, &gid) == 0, uid == getuid() else {
        throw RuntimeFailure("Control peer has a different owner.")
      }
      guard let request = try await channel.receiveAsync() else { return }
      switch request.operation {
      case "exec":
        guard let exec = request.exec else { throw RuntimeFailure("Missing exec request.") }
        try await vm.exec(exec, channel: channel)
      case "network":
        guard let socket = request.packetSocketPath, let key = request.data else {
          throw RuntimeFailure("Missing network lease configuration.")
        }
        let id = try await vm.network.start(socketPath: socket, key: key)
        do { try channel.send(.init(operation: "ready")) } catch {
          await vm.network.stop(id: id)
          throw error
        }
        let disconnect = Task.detached {
          _ = try? await channel.receiveAsync()
          await self.vm.network.stop(id: id)
        }
        let status = await vm.network.wait(id: id)
        try? channel.send(.init(operation: "stderr", data: status.diagnostic))
        try? channel.send(.init(operation: "exit", exitCode: status.code))
        channel.close()
        _ = await disconnect.value
      case "stop":
        guard await stop() else {
          throw RuntimeFailure("Workspace VM could not be stopped cleanly.")
        }
        try channel.send(.init(operation: "exit", exitCode: 0))
      case "status": try channel.send(.init(operation: "ready"))
      default: throw RuntimeFailure("Unknown runtime control operation.")
      }
    } catch {
      // Exec args, environment and network credentials never appear in diagnostics.
      let message = (error as? RuntimeFailure)?.description ?? "Runtime operation failed."
      try? channel.send(.init(operation: "error", message: message))
    }
  }
}

import Containerization
import ContainerizationOS
import Darwin
import Foundation
import Virtualization

/// The VM owns the persistent disk for its entire lifetime; stop never deletes it.
final class WorkspaceVM: @unchecked Sendable {
  let container: LinuxContainer
  let network: NetworkLease
  let configuration: ServeConfiguration
  private let diskLock: FileHandle

  init(configuration: ServeConfiguration) throws {
    try configuration.validate()
    self.configuration = configuration
    var root = stat()
    guard lstat(configuration.rootfsPath, &root) == 0, root.st_mode & S_IFMT == S_IFREG else {
      throw RuntimeFailure("Persistent root filesystem is unavailable.")
    }
    // Virtualization.framework takes its own lock on the disk image. Lock a
    // sidecar for runtime ownership instead of conflicting with VZ's disk lock.
    let descriptor = open(
      configuration.rootfsPath + ".lock", O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW, 0o600)
    guard descriptor >= 0 else {
      throw RuntimeFailure("Persistent root filesystem lock is unavailable.")
    }
    diskLock = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
    guard flock(descriptor, LOCK_EX | LOCK_NB) == 0 else {
      throw RuntimeFailure("Persistent root filesystem is already in use.")
    }
    let nic = try WorkspaceNIC()
    network = NetworkLease(nic: nic, executable: configuration.gatewayExecutablePath)
    let manager = VZVirtualMachineManager(
      kernel: Kernel(path: URL(fileURLWithPath: configuration.kernelPath), platform: .linuxArm),
      initialFilesystem: .block(
        format: "ext4", source: configuration.initfsPath, destination: "/", options: ["ro"]))
    var config = LinuxContainer.Configuration()
    config.cpus = configuration.cpus
    config.memoryInBytes = try WorkspaceMemoryBudget.guestBytes(
      requested: configuration.memoryBytes,
      hostPhysicalBytes: ProcessInfo.processInfo.physicalMemory,
      maximumVirtualMachineBytes: VZVirtualMachineConfiguration.maximumAllowedMemorySize)
    config.memoryOverhead = WorkspaceMemoryBudget.virtualMachineOverheadBytes
    config.hostname = configuration.hostname ?? configuration.id
    config.interfaces = [nic]
    config.dns = DNS(
      nameservers: ["100.64.0.1", "fd00:4753:4e57::1"], options: ["timeout:2", "attempts:2"])
    config.process.arguments = configuration.initialArguments ?? ["/sbin/init"]
    config.process.capabilities = .allCapabilities
    config.maskedPaths = []
    config.readonlyPaths = []
    config.process.environmentVariables = [
      "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin", "container=asura",
    ]
    config.mounts += configuration.mounts.map {
      .share(source: $0.source, destination: $0.destination, options: $0.readOnly ? ["ro"] : [])
    }
    container = try LinuxContainer(
      configuration.id,
      rootfs: .block(format: "ext4", source: configuration.rootfsPath, destination: "/"),
      vmm: manager, configuration: config)
  }

  func start() async throws {
    try await container.create()
    try await container.start()
  }

  func stop() async throws {
    await network.stop()
    try await container.stop()
  }

  func exec(_ request: ExecRequest, channel: UnixChannel) async throws {
    try request.validate()
    let input = ProcessInput()
    var config = LinuxProcessConfiguration()
    config.arguments = request.arguments
    config.environmentVariables = request.environment.map { "\($0.key)=\($0.value)" }
    if request.environment["PATH"] == nil {
      config.environmentVariables.append("PATH=\(LinuxProcessConfiguration.defaultPath)")
    }
    config.workingDirectory = request.workingDirectory
    config.user = .init(uid: request.userID, gid: request.groupID)
    config.user.additionalGids = request.supplementaryGroups ?? []
    config.terminal = request.terminal
    config.stdin = input
    config.stdout = ProcessOutput(channel: channel, operation: "stdout")
    if !request.terminal { config.stderr = ProcessOutput(channel: channel, operation: "stderr") }
    let process = try await container.exec(UUID().uuidString, configuration: config)
    do {
      try await process.start()
      if request.terminal {
        try await process.resize(to: .init(width: request.columns, height: request.rows))
      }
      try channel.send(.init(operation: "started"))
      let reader = Task.detached {
        do {
          while let record = try await channel.receiveAsync() {
            switch record.operation {
            case "stdin":
              guard let data = record.data else { throw RuntimeFailure("Missing stdin payload.") }
              guard data.count <= 32768 else { throw RuntimeFailure("Stdin chunk exceeds 32 KiB.") }
              try await input.write(data)
            case "eof": await input.finish()
            case "resize":
              guard let cols = record.columns, let rows = record.rows, cols > 0, rows > 0 else {
                throw RuntimeFailure("Invalid terminal size.")
              }
              try await process.resize(to: .init(width: cols, height: rows))
            default: throw RuntimeFailure("Unexpected exec control record.")
            }
          }
        } catch {
          try? channel.send(.init(operation: "error", message: "Exec control channel failed."))
        }
        await input.abort()
        try? await process.kill(.kill)
      }
      let status = try await process.wait()
      await input.abort()
      try channel.send(.init(operation: "exit", exitCode: status.exitCode))
      channel.close()
      _ = await reader.value
      try await process.delete()
    } catch {
      await input.abort()
      try? await process.kill(.kill)
      try? await process.delete()
      throw error
    }
  }
}

private struct ProcessOutput: Writer {
  let channel: UnixChannel
  let operation: String
  func write(_ data: Data) throws {
    for offset in stride(from: 0, to: data.count, by: 32768) {
      try channel.send(
        .init(
          operation: operation, data: data.subdata(in: offset..<min(offset + 32768, data.count))))
    }
  }
  func close() throws {}
}

import Containerization
import ContainerizationOCI
import ContainerizationOS
import Darwin
import Foundation

@main
struct RuntimeCommand {
  static func main() async {
    signal(SIGPIPE, SIG_IGN)
    do { exit(try await run(Array(CommandLine.arguments.dropFirst()))) } catch {
      let message = (error as? RuntimeFailure)?.description ?? String(describing: error)
      FileHandle.standardError.write(Data("workspace-runtime: \(message)\n".utf8))
      exit(1)
    }
  }

  static func run(_ arguments: [String]) async throws -> Int32 {
    guard let command = arguments.first else {
      throw RuntimeFailure(
        "Expected serve, exec, network, stop, status, prepare or prepare-initfs.")
    }
    var options: [String: String] = [:]
    var position = 1
    while position < arguments.count {
      guard arguments[position].hasPrefix("--"), position + 1 < arguments.count,
        options[arguments[position]] == nil
      else { throw RuntimeFailure("Invalid command options.") }
      options[arguments[position]] = arguments[position + 1]
      position += 2
    }
    func required(_ key: String) throws -> String {
      guard let value = options[key], !value.isEmpty else {
        throw RuntimeFailure("Missing \(key).")
      }
      return value
    }
    switch command {
    case "serve":
      let bytes = try Data(contentsOf: URL(fileURLWithPath: required("--config")))
      guard bytes.count <= UnixChannel.maximumRecordBytes else {
        throw RuntimeFailure("Runtime configuration is too large.")
      }
      let configuration = try JSONDecoder().decode(ServeConfiguration.self, from: bytes)
      let server = try RuntimeServer(configuration: configuration)
      let owner = OwnerLifetime { Task { await server.stop() } }
      defer { withExtendedLifetime(owner) {} }
      signal(SIGINT, SIG_IGN)
      signal(SIGTERM, SIG_IGN)
      let sources = [SIGINT, SIGTERM].map { number in
        let source = DispatchSource.makeSignalSource(signal: number, queue: .global())
        source.setEventHandler { Task { await server.stop() } }
        source.resume()
        return source
      }
      defer { sources.forEach { $0.cancel() } }
      try await server.run()
      return 0
    case "prepare", "prepare-initfs":
      if command == "prepare-initfs", options["--capacity-mib"] != nil {
        throw RuntimeFailure("Initfs capacity is supplied by its pinned image.")
      }
      let output = try required(command == "prepare" ? "--rootfs" : "--output")
      try await prepare(
        image: required("--image"), output: output, stateDirectory: required("--state-directory"),
        initfs: command == "prepare-initfs",
        capacity: rootFilesystemCapacity(options["--capacity-mib"]))
      print("READY v1")
      return 0
    case "exec":
      guard let data = Data(base64Encoded: try required("--request")),
        data.count <= UnixChannel.maximumRecordBytes
      else { throw RuntimeFailure("Invalid encoded exec request.") }
      let request = try JSONDecoder().decode(ExecRequest.self, from: data)
      try request.validate()
      return try await execClient(socket: required("--socket"), request: request)
    case "network":
      let key = FileHandle.standardInput.readData(ofLength: 33)
      guard key.count == 32 else {
        throw RuntimeFailure("Network lease requires exactly 32 stdin bytes followed by EOF.")
      }
      let channel = try UnixChannel.connect(path: required("--socket"))
      defer { channel.close() }
      let owner = OwnerLifetime { channel.close() }
      defer { withExtendedLifetime(owner) {} }
      try channel.send(
        .init(operation: "network", packetSocketPath: required("--packet-socket"), data: key))
      return try receiveClient(channel, ready: true)
    case "stop", "status":
      let channel = try UnixChannel.connect(path: required("--socket"))
      defer { channel.close() }
      try channel.send(.init(operation: command))
      return try receiveClient(
        channel, ready: command == "status", finishOnReady: command == "status")
    default: throw RuntimeFailure("Unknown runtime command.")
    }
  }

  // Interactive workspaces retain their existing 32 GiB disk. A service caller
  // can request a smaller bounded disk so verified immutable templates stay cheap.
  static func rootFilesystemCapacity(_ value: String?) throws -> UInt64 {
    guard let value else { return 32 * 1024 * 1024 * 1024 }
    guard !value.isEmpty, value.allSatisfy({ $0.isASCII && $0.isNumber }),
      let mib = UInt64(value), (1024...32768).contains(mib)
    else { throw RuntimeFailure("Filesystem capacity must be 1024–32768 MiB.") }
    return mib * 1024 * 1024
  }

  private static func prepare(
    image: String, output: String, stateDirectory: String, initfs: Bool, capacity: UInt64
  )
    async throws
  {
    guard output.hasPrefix("/"), stateDirectory.hasPrefix("/") else {
      throw RuntimeFailure("Image paths must be absolute.")
    }
    guard !FileManager.default.fileExists(atPath: output) else {
      throw RuntimeFailure("Refusing to overwrite an existing filesystem.")
    }
    try FileManager.default.createDirectory(
      atPath: stateDirectory, withIntermediateDirectories: true,
      attributes: [.posixPermissions: 0o700])
    let store = try ImageStore(path: URL(fileURLWithPath: stateDirectory))
    let target = URL(fileURLWithPath: output)
    let temporary = target.deletingLastPathComponent().appendingPathComponent(
      ".\(target.lastPathComponent).\(UUID().uuidString).partial")
    defer { try? FileManager.default.removeItem(at: temporary) }
    if initfs {
      let image = try await store.getInitImage(reference: image)
      _ = try await image.initBlock(at: temporary, for: .linuxArm)
    } else {
      let image = try await store.get(reference: image, pull: true)
      _ = try await EXT4Unpacker(capacityInBytes: capacity).unpack(
        image, for: .init(arch: "arm64", os: "linux"), at: temporary)
    }
    guard chmod(temporary.path, 0o600) == 0 else { throw POSIXError(.EIO) }
    let disk = try FileHandle(forWritingTo: temporary)
    try disk.synchronize()
    try disk.close()
    // A cancelled unpack can leave only a uniquely named partial, never a disk
    // that the caller could mistake for a completed root filesystem.
    guard renamex_np(temporary.path, target.path, UInt32(RENAME_EXCL)) == 0 else {
      throw RuntimeFailure(
        "Could not publish the prepared filesystem without replacing an existing file.")
    }
  }

  private static func execClient(socket: String, request: ExecRequest) async throws -> Int32 {
    let channel = try UnixChannel.connect(path: socket)
    defer { channel.close() }
    var terminal: Terminal?
    if request.terminal {
      terminal = try Terminal.current
      try terminal?.setraw()
    }
    defer { terminal?.tryReset() }
    try channel.send(.init(operation: "exec", exec: request))
    let resize = DispatchSource.makeSignalSource(signal: SIGWINCH, queue: .global())
    signal(SIGWINCH, SIG_IGN)
    resize.setEventHandler {
      guard let size = try? Terminal.current.size else { return }
      try? channel.send(.init(operation: "resize", columns: size.width, rows: size.height))
    }
    resize.resume()
    defer { resize.cancel() }
    DispatchQueue.global().async {
      var bytes = [UInt8](repeating: 0, count: 32768)
      while true {
        // A fill-buffer read holds keystrokes and TCP handshakes until EOF.
        // Forward whatever is available without waiting for the buffer to fill.
        let count = Darwin.read(STDIN_FILENO, &bytes, bytes.count)
        if count < 0 && errno == EINTR { continue }
        guard count >= 0 else {
          channel.close()
          break
        }
        do {
          if count == 0 {
            try channel.send(.init(operation: "eof"))
            break
          }
          try channel.send(.init(operation: "stdin", data: Data(bytes.prefix(count))))
        } catch {
          channel.close()
          break
        }
      }
    }
    return try receiveClient(channel)
  }

  private static func receiveClient(
    _ channel: UnixChannel, ready: Bool = false, finishOnReady: Bool = false
  ) throws -> Int32 {
    while let record = try channel.receive() {
      switch record.operation {
      case "ready":
        if ready {
          print("READY v1")
          fflush(stdout)
        }
        if finishOnReady { return 0 }
      case "started": break
      case "stdout":
        if let bytes = record.data { try FileHandle.standardOutput.write(contentsOf: bytes) }
      case "stderr":
        if let bytes = record.data { try FileHandle.standardError.write(contentsOf: bytes) }
      case "exit": return record.exitCode ?? 1
      case "error": throw RuntimeFailure(record.message ?? "Runtime operation failed.")
      default: throw RuntimeFailure("Unexpected runtime response.")
      }
    }
    throw RuntimeFailure("Runtime control channel closed unexpectedly.")
  }
}

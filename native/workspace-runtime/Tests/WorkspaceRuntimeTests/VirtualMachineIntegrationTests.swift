import Darwin
import Foundation
import Testing

@testable import WorkspaceRuntime

/// Opt-in: boots only new test disks, never discovers or changes user workspaces.
@Test(.enabled(if: ProcessInfo.processInfo.environment["ASURA_RUNTIME_TEST_ASSETS"] != nil))
func sdkVirtualNICPersistentDiskAndTerminal() async throws {
  let assets = ProcessInfo.processInfo.environment["ASURA_RUNTIME_TEST_ASSETS"]!
  let directory = URL(fileURLWithPath: "/tmp/gs-sdk-\(UUID().uuidString.prefix(8))")
  try FileManager.default.createDirectory(
    at: directory, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
  defer { try? FileManager.default.removeItem(at: directory) }
  let executable =
    ProcessInfo.processInfo.environment["ASURA_RUNTIME_TEST_EXECUTABLE"] ?? assets
    + "/workspace-runtime"
  let rootfs = directory.appendingPathComponent("rootfs.ext4").path
  let socket = directory.appendingPathComponent("control.sock").path
  let prepare = try runCommand(
    executable,
    [
      "prepare", "--image", "docker.io/library/alpine:3.22.1", "--rootfs", rootfs,
      "--state-directory", directory.appendingPathComponent("images").path,
    ])
  #expect(prepare.0 == 0, "\(prepare.1)")
  let writableShare = directory.appendingPathComponent("writable")
  let readonlyShare = directory.appendingPathComponent("readonly")
  for share in [writableShare, readonlyShare] {
    try FileManager.default.createDirectory(
      at: share, withIntermediateDirectories: false,
      attributes: [.posixPermissions: 0o700])
  }
  try Data("read-only".utf8).write(to: readonlyShare.appendingPathComponent("input"))
  let config = ServeConfiguration(
    id: "sdk-integration", controlSocketPath: socket, rootfsPath: rootfs,
    kernelPath: assets + "/kernel.bin", initfsPath: assets + "/initfs.ext4",
    gatewayExecutablePath: ProcessInfo.processInfo.environment["ASURA_RUNTIME_TEST_GATEWAY"]
      ?? "/usr/bin/false",
    cpus: 2, memoryBytes: 536_870_912, hostname: "sdk-integration",
    mounts: [
      .init(source: writableShare.path, destination: "/host-rw", readOnly: false),
      .init(source: readonlyShare.path, destination: "/host-ro", readOnly: true),
    ],
    initialArguments: ["/bin/sleep", "infinity"])
  let configPath = directory.appendingPathComponent("config.json")
  try JSONEncoder().encode(config).write(to: configPath)
  for iteration in 0..<2 {
    let (server, output) = try launch(executable, ["serve", "--config", configPath.path])
    defer {
      if server.isRunning {
        server.terminate()
        server.waitUntilExit()
      }
    }
    #expect(try readLine(output) == "READY v1")
    let command =
      iteration == 0
      ? "printf persistent > /sdk-proof; ip -4 route; ip -6 route; test ! -e /opt/asura/bin/workspace-gateway"
      : "test \"$(cat /sdk-proof)\" = persistent"
    let result = try await guestExec(socket: socket, command: command)
    #expect(result.code == 0, "\(result.output)")
    if iteration == 0 {
      let shares = try await guestExec(
        socket: socket,
        command:
          "printf host-write > /host-rw/output; test \"$(cat /host-ro/input)\" = read-only && ! (printf denied > /host-ro/output) 2>/dev/null",
        userID: getuid(), groupID: getgid())
      #expect(shares.code == 0, "\(shares.output)")
      #expect(
        try Data(contentsOf: writableShare.appendingPathComponent("output"))
          == Data("host-write".utf8))
      #expect(
        !FileManager.default.fileExists(atPath: readonlyShare.appendingPathComponent("output").path)
      )
      try await withThrowingTaskGroup(of: Int32.self) { group in
        for _ in 0..<24 {
          group.addTask {
            try await guestExec(socket: socket, command: "sleep 0.1; printf panel").code
          }
        }
        for try await code in group { #expect(code == 0) }
      }
      #expect(result.output.contains("default via 100.64.0.1"))
      #expect(result.output.contains("default via fd00:4753:4e57::1"))
      let blocked = try await guestExec(
        socket: socket, command: "! timeout 3 wget -qO- http://1.1.1.1")
      #expect(blocked.code == 0)
      let terminal = try await guestExec(
        socket: socket, command: "read answer; test \"$answer\" = typed && test -t 0 && stty size",
        terminal: true)
      #expect(terminal.code == 0)
      #expect(terminal.output.contains("31 101"))
      if let gateway = ProcessInfo.processInfo.environment["ASURA_RUNTIME_TEST_GATEWAY"] {
        try await verifyHostOwnedRoute(
          executable: executable, gateway: gateway, socket: socket,
          packetSocket: directory.appendingPathComponent("packet.sock").path)
      }
    }
    let stopped = try runCommand(executable, ["stop", "--socket", socket])
    #expect(stopped.0 == 0, "\(stopped.1)")
    server.waitUntilExit()
    #expect(server.terminationStatus == 0)
    #expect(FileManager.default.fileExists(atPath: rootfs))
  }
}

private func verifyHostOwnedRoute(
  executable: String, gateway: String, socket: String, packetSocket: String
) async throws {
  let key = Data((0..<32).map { _ in UInt8.random(in: 0...255) })
  let (lease, leaseOutput) = try launch(
    executable, ["network", "--socket", socket, "--packet-socket", packetSocket], input: key)
  defer {
    if lease.isRunning {
      lease.terminate()
      lease.waitUntilExit()
    }
  }
  #expect(try readLine(leaseOutput) == "READY v1")
  let (route, routeOutput) = try launch(
    gateway,
    ["host", "--socket", packetSocket, "--mode", "direct", "--mtu", "1280", "--dns", "1.1.1.1"],
    input: key)
  defer {
    if route.isRunning {
      route.terminate()
      route.waitUntilExit()
    }
  }
  #expect(try readLine(routeOutput).hasPrefix("READY v1"))
  let response = try await guestExec(
    socket: socket,
    command: "nslookup example.com && wget -T 10 -qO- http://example.com | grep -q 'Example Domain'"
  )
  #expect(response.code == 0, "\(response.output)")
  // Package downloads burst more frames than the virtual NIC receive buffer can hold.
  let download = try await guestExec(
    socket: socket,
    command:
      "wget -T 30 -qO /tmp/package-index http://ports.ubuntu.com/ubuntu-ports/dists/noble/main/binary-arm64/Packages.xz && test \"$(wc -c < /tmp/package-index)\" -gt 1048576"
  )
  #expect(download.code == 0, "\(download.output)")
  #expect(lease.isRunning, "Network lease exited during the package-index download")
  #expect(route.isRunning, "Host route exited during the package-index download")
  lease.terminate()
  lease.waitUntilExit()
  let blocked = try await guestExec(socket: socket, command: "! timeout 3 wget -qO- http://1.1.1.1")
  #expect(blocked.code == 0)
}

private func guestExec(
  socket: String, command: String, terminal: Bool = false, userID: UInt32 = 0, groupID: UInt32 = 0
) async throws -> (
  code: Int32, output: String
) {
  let channel = try UnixChannel.connect(path: socket)
  defer { channel.close() }
  let request = ExecRequest(
    arguments: ["/bin/sh", "-c", command], environment: [:], workingDirectory: "/",
    userID: userID, groupID: groupID, supplementaryGroups: nil, terminal: terminal, columns: 101,
    rows: 31)
  try channel.send(.init(operation: "exec", exec: request))
  if !terminal { try channel.send(.init(operation: "eof")) }
  var output = Data()
  while let record = try await channel.receiveAsync() {
    if terminal && record.operation == "started" {
      try channel.send(.init(operation: "resize", columns: 101, rows: 31))
      try channel.send(.init(operation: "stdin", data: Data("typed\n".utf8)))
    }
    if let data = record.data { output.append(data) }
    if record.operation == "exit" {
      return (record.exitCode ?? -1, String(decoding: output, as: UTF8.self))
    }
    if record.operation == "error" { throw RuntimeFailure(record.message ?? "Exec failed") }
  }
  throw RuntimeFailure("Exec closed without exit status")
}

private func launch(_ executable: String, _ arguments: [String], input: Data? = nil) throws -> (
  Process, FileHandle
) {
  let process = Process()
  process.executableURL = URL(fileURLWithPath: executable)
  process.arguments = arguments
  let pipe = Pipe()
  process.standardOutput = pipe
  process.standardError = FileHandle.standardError
  let stdin = Pipe()
  if input != nil { process.standardInput = stdin }
  try process.run()
  if let input {
    try stdin.fileHandleForWriting.write(contentsOf: input)
    try stdin.fileHandleForWriting.close()
  }
  return (process, pipe.fileHandleForReading)
}

private func runCommand(_ executable: String, _ arguments: [String]) throws -> (Int32, String) {
  let (process, output) = try launch(executable, arguments)
  let bytes = output.readDataToEndOfFile()
  process.waitUntilExit()
  return (process.terminationStatus, String(decoding: bytes, as: UTF8.self))
}

private func readLine(_ handle: FileHandle) throws -> String {
  var line = Data()
  let deadline = Date().addingTimeInterval(60)
  while line.count < 4096 && Date() < deadline {
    var fd = pollfd(fd: handle.fileDescriptor, events: Int16(POLLIN), revents: 0)
    guard poll(&fd, 1, 1000) > 0 else { continue }
    guard let byte = try handle.read(upToCount: 1), !byte.isEmpty else { break }
    if byte[0] == 10 { return String(decoding: line, as: UTF8.self) }
    line.append(byte)
  }
  throw RuntimeFailure("Runtime did not emit a complete readiness line.")
}

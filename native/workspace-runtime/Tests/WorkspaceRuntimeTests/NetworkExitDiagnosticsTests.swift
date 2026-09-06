import Darwin
import Foundation
import Testing

@testable import WorkspaceRuntime

@Test(arguments: [
  ("receive provider packet: read packet frame header: EOF", "provider-channel-closed"),
  ("send provider packet: write unix /private/secret.sock: broken pipe", "provider-channel-closed"),
  ("receive provider packet: i/o timeout", "provider-channel-failed"),
  ("establish provider channel: i/o timeout", "provider-handshake-failed"),
  ("write workspace NIC: no buffer space available", "workspace-nic-congestion"),
  ("read workspace NIC: bad file descriptor", "workspace-nic-failed"),
  ("receive provider packet: packet channel authentication failed", "protocol-authentication"),
  ("receive provider packet: packet channel sequence is invalid", "protocol-sequence"),
  ("receive provider packet: packet channel frame is malformed", "protocol-malformed"),
  ("receive provider packet: packet channel frame is too large", "protocol-malformed"),
  ("receive provider packet: packet channel version is unsupported", "protocol-malformed"),
  ("password=secret", "unknown"),
])
func gatewayDiagnosticsClassifiesOnlyKnownReasons(message: String, expected: String) {
  #expect(NetworkExitDiagnostics.classify("workspace network gateway: " + message) == expected)
}

@Test func gatewayDiagnosticsDiscardsRawAndOversizedOutput() throws {
  let pipe = Pipe()
  let diagnostics = NetworkExitDiagnostics(input: pipe.fileHandleForReading)
  try pipe.fileHandleForWriting.write(contentsOf: Data(repeating: 120, count: 200_000))
  try pipe.fileHandleForWriting.write(
    contentsOf: Data("\npanic: private password and a stack trace\n".utf8))
  try pipe.fileHandleForWriting.close()
  let result = NetworkExit(code: 2, reason: diagnostics.finish())
  #expect(result.diagnostic == Data("GHOSTSHELL_GATEWAY_EXIT_REASON=runtime-panic\n".utf8))
  #expect(NetworkExitDiagnostics.classify("password=secret") == "unknown")
}

@Test func gatewayDiagnosticsDoesNotWaitForInheritedStderrWriter() throws {
  let pipe = Pipe()
  defer { try? pipe.fileHandleForWriting.close() }
  let diagnostics = NetworkExitDiagnostics(input: pipe.fileHandleForReading)
  try pipe.fileHandleForWriting.write(
    contentsOf: Data("workspace network gateway: receive provider packet: EOF\n".utf8))
  let started = ContinuousClock.now
  #expect(diagnostics.finish() == "provider-channel-closed")
  #expect(started.duration(to: .now) < .seconds(1))
}

@Test func networkCLIForwardsReasonAndSignalExitCode() throws {
  let directory = "/tmp/gs-exit-\(UUID().uuidString.prefix(8))"
  try FileManager.default.createDirectory(
    atPath: directory, withIntermediateDirectories: false,
    attributes: [.posixPermissions: 0o700])
  defer { try? FileManager.default.removeItem(atPath: directory) }
  let socket = directory + "/control.sock"
  let listener = try UnixChannel.listen(path: socket)
  defer { Darwin.close(listener) }
  let testBundle = try #require(CommandLine.arguments.first { $0.contains(".xctest") })
  var executable = URL(fileURLWithPath: testBundle).deletingLastPathComponent()
  while executable.lastPathComponent != "debug" && executable.lastPathComponent != "release"
    && executable.path != "/"
  {
    executable.deleteLastPathComponent()
  }
  executable.appendPathComponent("workspace-runtime")
  let process = Process()
  process.executableURL = executable
  process.arguments = [
    "network", "--socket", socket, "--packet-socket", directory + "/packet.sock",
  ]
  let input = Pipe()
  let errors = Pipe()
  process.standardInput = input
  process.standardOutput = FileHandle.nullDevice
  process.standardError = errors
  try process.run()
  defer {
    if process.isRunning {
      process.terminate()
      process.waitUntilExit()
    }
  }
  try errors.fileHandleForWriting.close()
  try input.fileHandleForWriting.write(contentsOf: Data(repeating: 24, count: 32))
  try input.fileHandleForWriting.close()
  var incoming = pollfd(fd: listener, events: Int16(POLLIN), revents: 0)
  try #require(poll(&incoming, 1, 5000) > 0)
  let accepted = accept(listener, nil, nil)
  try #require(accepted >= 0)
  let channel = UnixChannel(accepted)
  defer { channel.close() }
  #expect(try channel.receive()?.operation == "network")
  let result = NetworkExit(code: 137, reason: "unknown")
  try channel.send(.init(operation: "ready"))
  try channel.send(.init(operation: "stderr", data: result.diagnostic))
  try channel.send(.init(operation: "exit", exitCode: result.code))
  process.waitUntilExit()
  #expect(process.terminationStatus == 137)
  #expect(try errors.fileHandleForReading.readToEnd() == result.diagnostic)
}

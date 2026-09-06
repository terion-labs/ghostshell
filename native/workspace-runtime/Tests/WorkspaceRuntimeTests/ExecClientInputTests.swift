import Darwin
import Foundation
import Testing

@testable import WorkspaceRuntime

@Test func execCLIForwardsShortWritesBeforeStdinEOF() throws {
  let directory = "/tmp/gs-input-\(UUID().uuidString.prefix(8))"
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
  let request = ExecRequest(
    arguments: ["/bin/cat"], environment: [:], workingDirectory: "/", userID: 0,
    groupID: 0, supplementaryGroups: nil, terminal: false, columns: 80, rows: 24)
  let process = Process()
  process.executableURL = executable
  process.arguments = [
    "exec", "--socket", socket, "--request",
    try JSONEncoder().encode(request).base64EncodedString(),
  ]
  let input = Pipe()
  process.standardInput = input
  process.standardOutput = FileHandle.nullDevice
  process.standardError = FileHandle.standardError
  try process.run()
  defer {
    if process.isRunning {
      process.terminate()
      process.waitUntilExit()
    }
  }
  var incoming = pollfd(fd: listener, events: Int16(POLLIN), revents: 0)
  try #require(poll(&incoming, 1, 5000) > 0)
  let accepted = accept(listener, nil, nil)
  try #require(accepted >= 0)
  let channel = UnixChannel(accepted)
  defer { channel.close() }
  #expect(try channel.receive()?.operation == "exec")
  // A keystroke and a small binary tunnel write must arrive while stdin stays open.
  for payload in [Data([97]), Data([0x16, 0x03, 0x01, 0x00, 0x04])] {
    try input.fileHandleForWriting.write(contentsOf: payload)
    var readable = pollfd(fd: accepted, events: Int16(POLLIN), revents: 0)
    try #require(poll(&readable, 1, 2000) > 0)
    let received = try channel.receive()
    #expect(received?.operation == "stdin")
    #expect(received?.data == payload)
  }
  try input.fileHandleForWriting.close()
  var ended = pollfd(fd: accepted, events: Int16(POLLIN), revents: 0)
  try #require(poll(&ended, 1, 2000) > 0)
  #expect(try channel.receive()?.operation == "eof")
  try channel.send(.init(operation: "exit", exitCode: 0))
  process.waitUntilExit()
  #expect(process.terminationStatus == 0)
}

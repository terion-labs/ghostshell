import Darwin
import Foundation
import Testing

@testable import WorkspaceRuntime

@Test func networkCLIClosesLeaseWithinTwoSecondsOfOwnerDeath() throws {
  let directory = "/tmp/gs-owner-\(UUID().uuidString.prefix(8))"
  try FileManager.default.createDirectory(
    atPath: directory, withIntermediateDirectories: false,
    attributes: [.posixPermissions: 0o700])
  defer { try? FileManager.default.removeItem(atPath: directory) }
  let socket = directory + "/control.sock"
  let listener = try UnixChannel.listen(path: socket)
  defer { Darwin.close(listener) }
  let testBundlePath = try #require(CommandLine.arguments.first { $0.contains(".xctest") })
  var executable = URL(fileURLWithPath: testBundlePath).deletingLastPathComponent()
  while executable.lastPathComponent != "debug" && executable.lastPathComponent != "release"
    && executable.path != "/"
  {
    executable.deleteLastPathComponent()
  }
  executable.appendPathComponent("workspace-runtime")
  try #require(FileManager.default.isExecutableFile(atPath: executable.path))
  let owner = Process()
  owner.executableURL = URL(fileURLWithPath: "/bin/sh")
  owner.arguments = [
    "-c", "\"$1\" network --socket \"$2\" --packet-socket \"$3\" <&0 & wait", "owner-test",
    executable.path, socket, directory + "/packet.sock",
  ]
  let input = Pipe()
  let output = Pipe()
  owner.standardInput = input
  owner.standardOutput = output
  owner.standardError = FileHandle.standardError
  try owner.run()
  defer {
    if owner.isRunning {
      owner.terminate()
      owner.waitUntilExit()
    }
  }
  try input.fileHandleForWriting.write(contentsOf: Data(repeating: 42, count: 32))
  try input.fileHandleForWriting.close()
  var incoming = pollfd(fd: listener, events: Int16(POLLIN), revents: 0)
  try #require(poll(&incoming, 1, 5000) > 0)
  let accepted = accept(listener, nil, nil)
  try #require(accepted >= 0)
  let channel = UnixChannel(accepted)
  defer { channel.close() }
  let request = try channel.receive()
  #expect(request?.operation == "network")
  try channel.send(.init(operation: "ready"))
  owner.terminate()
  owner.waitUntilExit()
  var ended = pollfd(fd: accepted, events: Int16(POLLIN), revents: 0)
  try #require(poll(&ended, 1, 2000) > 0)
  #expect(try channel.receive() == nil)
}

import Darwin
import Foundation
import Testing
import Virtualization

@testable import WorkspaceRuntime

@Test func nicUsesOnlyFileHandleAttachment() throws {
  let nic = try WorkspaceNIC()
  let device = try nic.device()
  #expect(device.attachment is VZFileHandleNetworkDeviceAttachment)
  #expect(
    (device.attachment as? VZFileHandleNetworkDeviceAttachment)?.maximumTransmissionUnit == 1500)
  #expect(nic.mtu == 1280)
  #expect(nic.ipv4Gateway?.description == "100.64.0.1")
  #expect(nic.ipv6Gateway?.description == "fd00:4753:4e57::1")
}

@Test func controlRecordsRoundTripBinaryWithoutShellParsing() throws {
  var sockets: [Int32] = [-1, -1]
  #expect(socketpair(AF_UNIX, SOCK_STREAM, 0, &sockets) == 0)
  let first = UnixChannel(sockets[0])
  let second = UnixChannel(sockets[1])
  let payload = Data([0, 10, 255, 34, 92])
  try first.send(.init(operation: "stdin", data: payload))
  let received = try second.receive()
  #expect(received?.operation == "stdin")
  #expect(received?.data == payload)
}

@Test func rejectRelativeAndOversizedControlPaths() {
  #expect(throws: (any Error).self) { try UnixChannel.validatePath("relative.sock") }
  #expect(throws: (any Error).self) {
    try UnixChannel.validatePath("/" + String(repeating: "a", count: 104))
  }
}

@Test func drainDiscardsPreviousGenerationFrames() throws {
  let nic = try WorkspaceNIC()
  try nic.guestHandle.write(contentsOf: Data([1, 2, 3]))
  nic.drain()
  var byte: UInt8 = 0
  #expect(recv(nic.hostHandle.fileDescriptor, &byte, 1, MSG_DONTWAIT) == -1)
  #expect(errno == EAGAIN)
}

@Test func malformedExecRejectedBeforeGuestLaunch() throws {
  let request = ExecRequest(
    arguments: ["relative"], environment: [:], workingDirectory: "/", userID: 0, groupID: 0,
    supplementaryGroups: nil, terminal: false, columns: 80, rows: 24)
  #expect(throws: (any Error).self) { try request.validate() }
}

@Test func closeUnblocksWaitingControlReader() async throws {
  var sockets: [Int32] = [-1, -1]
  #expect(socketpair(AF_UNIX, SOCK_STREAM, 0, &sockets) == 0)
  let first = UnixChannel(sockets[0])
  let second = UnixChannel(sockets[1])
  let reader = Task.detached { try first.receive() }
  first.close()
  #expect(try await reader.value == nil)
  second.close()
}

@Test func listenerRejectsPublicDirectoryAndPreservesExistingSocket() throws {
  let path = "/tmp/gs-socket-\(UUID().uuidString.prefix(8))"
  try FileManager.default.createDirectory(
    atPath: path, withIntermediateDirectories: false,
    attributes: [.posixPermissions: 0o700])
  defer { try? FileManager.default.removeItem(atPath: path) }
  let socket = path + "/control.sock"
  let listener = try UnixChannel.listen(path: socket)
  defer { Darwin.close(listener) }
  #expect(throws: (any Error).self) { _ = try UnixChannel.listen(path: socket) }
  #expect(FileManager.default.fileExists(atPath: socket))
  #expect(chmod(path, 0o755) == 0)
  #expect(throws: (any Error).self) { _ = try UnixChannel.listen(path: path + "/other.sock") }
}

@Test func stdinStreamPreservesChunksAndDrainsBeforeEOF() async throws {
  let input = ProcessInput()
  var iterator = input.stream().makeAsyncIterator()
  try await input.write(Data([1]))
  let second = Task { try await input.write(Data([2])) }
  #expect(await iterator.next() == Data([1]))
  try await second.value
  await input.finish()
  #expect(await iterator.next() == Data([2]))
  #expect(await iterator.next() == nil)
}

@Test func stdinAbortUnblocksPendingProducer() async throws {
  let input = ProcessInput()
  try await input.write(Data([1]))
  let producer = Task { try await input.write(Data([2])) }
  await input.abort()
  await #expect(throws: (any Error).self) { try await producer.value }
}

@Test func manyIdleReadersDoNotStarveCooperativeTasks() async throws {
  var channels: [(UnixChannel, UnixChannel)] = []
  for _ in 0..<48 {
    var sockets: [Int32] = [-1, -1]
    try #require(socketpair(AF_UNIX, SOCK_STREAM, 0, &sockets) == 0)
    channels.append((UnixChannel(sockets[0]), UnixChannel(sockets[1])))
  }
  let pairs = channels
  defer {
    pairs.forEach {
      $0.0.close()
      $0.1.close()
    }
  }
  DispatchQueue.global().asyncAfter(deadline: .now() + 5) {
    pairs.forEach {
      $0.0.close()
      $0.1.close()
    }
  }
  try await withThrowingTaskGroup(of: Void.self) { group in
    for pair in pairs {
      group.addTask {
        let record = try await pair.0.receiveAsync()
        #expect(record?.operation == "ready")
      }
    }
    group.addTask {
      try await Task.sleep(for: .milliseconds(30))
      for pair in pairs { try pair.1.send(.init(operation: "ready")) }
    }
    try await group.waitForAll()
  }
}

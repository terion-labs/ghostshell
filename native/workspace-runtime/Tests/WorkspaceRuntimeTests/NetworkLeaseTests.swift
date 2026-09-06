import Darwin
import Foundation
import Testing

@testable import WorkspaceRuntime

@Test(.enabled(if: ProcessInfo.processInfo.environment["GHOSTSHELL_RUNTIME_TEST_GATEWAY"] != nil))
func packetSocketCanBeReusedAfterGracefulStopAndCrash() throws {
  let executable = ProcessInfo.processInfo.environment["GHOSTSHELL_RUNTIME_TEST_GATEWAY"]!
  let directory = "/tmp/gs-lease-\(UUID().uuidString.prefix(8))"
  try FileManager.default.createDirectory(
    atPath: directory, withIntermediateDirectories: false,
    attributes: [.posixPermissions: 0o700])
  defer { try? FileManager.default.removeItem(atPath: directory) }
  let path = directory + "/packet.sock"
  let nic = try WorkspaceNIC()
  for crash in [false, true, false] {
    let child = try NetworkChild(
      executable: executable, socketPath: path, nic: nic,
      key: Data(repeating: 24, count: 32))
    #expect(FileManager.default.fileExists(atPath: path))
    if crash { #expect(kill(child.processIdentifier, SIGKILL) == 0) } else { child.terminate() }
    let code = child.wait()
    if crash { #expect(code == 137) }
    #expect(!FileManager.default.fileExists(atPath: path))
  }
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["GHOSTSHELL_RUNTIME_TEST_GATEWAY"] != nil))
func packetCleanupDoesNotDeleteReplacementSocket() throws {
  let executable = ProcessInfo.processInfo.environment["GHOSTSHELL_RUNTIME_TEST_GATEWAY"]!
  let directory = "/tmp/gs-inode-\(UUID().uuidString.prefix(8))"
  try FileManager.default.createDirectory(
    atPath: directory, withIntermediateDirectories: false,
    attributes: [.posixPermissions: 0o700])
  defer { try? FileManager.default.removeItem(atPath: directory) }
  let path = directory + "/packet.sock"
  let child = try NetworkChild(
    executable: executable, socketPath: path, nic: WorkspaceNIC(),
    key: Data(repeating: 24, count: 32))
  #expect(kill(child.processIdentifier, SIGKILL) == 0)
  #expect(unlink(path) == 0)
  let replacement = try UnixChannel.listen(path: path)
  defer { Darwin.close(replacement) }
  #expect(child.wait() == 137)
  #expect(FileManager.default.fileExists(atPath: path))
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["GHOSTSHELL_RUNTIME_TEST_GATEWAY"] != nil))
func packetGatewayExitKeepsChildReasonAndSignalStatus() async throws {
  let executable = ProcessInfo.processInfo.environment["GHOSTSHELL_RUNTIME_TEST_GATEWAY"]!
  let directory = "/tmp/gs-reason-\(UUID().uuidString.prefix(8))"
  try FileManager.default.createDirectory(
    atPath: directory, withIntermediateDirectories: false,
    attributes: [.posixPermissions: 0o700])
  defer { try? FileManager.default.removeItem(atPath: directory) }
  let path = directory + "/packet.sock"
  let nic = try WorkspaceNIC()
  for crash in [false, true] {
    let child = try NetworkChild(
      executable: executable, socketPath: path, nic: nic, key: Data(repeating: 24, count: 32))
    if crash {
      #expect(kill(child.processIdentifier, SIGKILL) == 0)
    } else {
      // Close before completing the provider handshake. Its fixed failure reason
      // must survive the SDK lease; its raw socket path must not.
      let provider = try UnixChannel.connect(path: path)
      provider.close()
    }
    let result = await child.waitAsync()
    #expect(result.code == (crash ? 137 : 1))
    #expect(result.reason == (crash ? "unknown" : "provider-handshake-failed"))
    #expect(!String(decoding: result.diagnostic, as: UTF8.self).contains(directory))
  }
}

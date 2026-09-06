import Foundation

struct RuntimeFailure: Error, CustomStringConvertible {
  let description: String
  init(_ message: String) { description = message }
}

/// Paths are explicit: persistent disks never live inside the replaceable bundle.
struct ServeConfiguration: Codable, Sendable {
  struct Share: Codable, Sendable {
    let source: String
    let destination: String
    let readOnly: Bool
  }
  let id: String
  let controlSocketPath: String
  let rootfsPath: String
  let kernelPath: String
  let initfsPath: String
  let gatewayExecutablePath: String
  let cpus: Int
  let memoryBytes: UInt64
  let hostname: String?
  let mounts: [Share]
  let initialArguments: [String]?

  func validate() throws {
    guard !id.isEmpty, id.count <= 64,
      id.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-") }),
      (1...64).contains(cpus), memoryBytes >= 268_435_456,
      [rootfsPath, kernelPath, initfsPath, gatewayExecutablePath].allSatisfy({
        $0.hasPrefix("/") && !$0.contains("\0")
      })
    else { throw RuntimeFailure("Invalid runtime configuration.") }
    try UnixChannel.validatePath(controlSocketPath)
    for mount in mounts {
      guard mount.source.hasPrefix("/"), mount.destination.hasPrefix("/"),
        !mount.source.contains("\0"), !mount.destination.contains("\0")
      else { throw RuntimeFailure("Mount paths must be absolute.") }
    }
  }
}

struct ExecRequest: Codable, Sendable {
  let arguments: [String]
  let environment: [String: String]
  let workingDirectory: String
  let userID: UInt32
  let groupID: UInt32
  let supplementaryGroups: [UInt32]?
  let terminal: Bool
  let columns: UInt16
  let rows: UInt16

  func validate() throws {
    guard !arguments.isEmpty, arguments[0].hasPrefix("/"),
      arguments.allSatisfy({ !$0.contains("\0") }),
      workingDirectory.hasPrefix("/"), !workingDirectory.contains("\0"),
      environment.allSatisfy({
        !$0.key.contains("=") && !$0.key.contains("\0") && !$0.value.contains("\0")
      }),
      !terminal || (columns > 0 && rows > 0)
    else { throw RuntimeFailure("Invalid structured exec request.") }
  }
}

/// Newline-delimited JSON. Byte streams are base64; each record is <= 1 MiB.
struct WireRecord: Codable, Sendable {
  var operation: String
  var exec: ExecRequest?
  var packetSocketPath: String?
  var data: Data?
  var columns: UInt16?
  var rows: UInt16?
  var exitCode: Int32?
  var message: String?
}

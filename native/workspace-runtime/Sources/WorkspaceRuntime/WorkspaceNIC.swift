import Containerization
import ContainerizationExtras
import Darwin
import Foundation
import Virtualization

/// The only NIC attachment is a socketpair. No vmnet or host NAT path exists.
final class WorkspaceNIC: Interface, VZInterface, @unchecked Sendable {
  let ipv4Address = try! CIDRv4("100.64.0.2/30")
  let ipv4Gateway: IPv4Address? = try! IPv4Address("100.64.0.1")
  let ipv6Address: CIDRv6? = try! CIDRv6("fd00:4753:4e57::2/126")
  let ipv6Gateway: IPv6Address? = try! IPv6Address("fd00:4753:4e57::1")
  let macAddress: MACAddress? = try! MACAddress("02:47:53:4e:57:02")
  let mtu: UInt32 = 1280
  let guestHandle: FileHandle
  let hostHandle: FileHandle

  init() throws {
    var sockets: [Int32] = [-1, -1]
    guard socketpair(AF_UNIX, SOCK_DGRAM, 0, &sockets) == 0 else { throw POSIXError(.EIO) }
    for fd in sockets { _ = fcntl(fd, F_SETFD, FD_CLOEXEC) }
    guestHandle = FileHandle(fileDescriptor: sockets[0], closeOnDealloc: true)
    hostHandle = FileHandle(fileDescriptor: sockets[1], closeOnDealloc: true)
  }

  func device() throws -> VZVirtioNetworkDeviceConfiguration {
    let device = VZVirtioNetworkDeviceConfiguration()
    device.macAddress = VZMACAddress(string: macAddress!.description)!
    let attachment = VZFileHandleNetworkDeviceAttachment(fileHandle: guestHandle)
    // Virtualization.framework requires a hardware ceiling of at least 1500.
    // The SDK still configures the Linux interface and routed IP packets at 1280.
    attachment.maximumTransmissionUnit = 1500
    device.attachment = attachment
    return device
  }

  /// Route generations never inherit queued traffic from the previous lease.
  func drain() {
    var buffer = [UInt8](repeating: 0, count: 65536)
    while recv(hostHandle.fileDescriptor, &buffer, buffer.count, MSG_DONTWAIT) > 0 {}
  }
}

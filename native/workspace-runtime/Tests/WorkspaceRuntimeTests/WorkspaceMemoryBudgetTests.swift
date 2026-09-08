import Foundation
import Testing

@testable import WorkspaceRuntime

@Test func automaticMemoryScalesWithHostAndAccountsForSDKOverhead() throws {
  let gib: UInt64 = 1024 * 1024 * 1024
  for hostGiB: UInt64 in [8, 16, 32, 64, 128] {
    let memory = try WorkspaceMemoryBudget.guestBytes(
      requested: nil, hostPhysicalBytes: hostGiB * gib,
      maximumVirtualMachineBytes: 256 * gib)
    #expect(memory + WorkspaceMemoryBudget.virtualMachineOverheadBytes == hostGiB * gib / 2)
    #expect(memory > gib)
  }
}

@Test func automaticMemoryHonorsFrameworkMaximumAndMiBAlignment() throws {
  let mib = WorkspaceMemoryBudget.mebibyte
  let memory = try WorkspaceMemoryBudget.guestBytes(
    requested: nil, hostPhysicalBytes: 16384 * mib + 777,
    maximumVirtualMachineBytes: 4096 * mib + 333)
  #expect(memory + WorkspaceMemoryBudget.virtualMachineOverheadBytes == 4096 * mib)
  #expect(memory % mib == 0)
}

@Test func automaticMemoryHasValidMinimumWithoutExceedingHost() throws {
  let mib = WorkspaceMemoryBudget.mebibyte
  #expect(
    try WorkspaceMemoryBudget.guestBytes(
      requested: nil, hostPhysicalBytes: 512 * mib, maximumVirtualMachineBytes: 512 * mib)
      == WorkspaceMemoryBudget.minimumGuestBytes)
  #expect(throws: RuntimeFailure.self) {
    try WorkspaceMemoryBudget.guestBytes(
      requested: nil, hostPhysicalBytes: 383 * mib, maximumVirtualMachineBytes: 512 * mib)
  }
}

@Test func explicitMemoryRetainsRequestedCeilingAndRejectsUnsupportedSizes() throws {
  let mib = WorkspaceMemoryBudget.mebibyte
  #expect(
    try WorkspaceMemoryBudget.guestBytes(
      requested: 512 * mib, hostPhysicalBytes: 8192 * mib,
      maximumVirtualMachineBytes: 8192 * mib) == 512 * mib)
  #expect(
    try WorkspaceMemoryBudget.guestBytes(
      requested: 512 * mib + 1, hostPhysicalBytes: 8192 * mib,
      maximumVirtualMachineBytes: 8192 * mib) == 513 * mib)
  for requested: UInt64 in [0, 255 * mib, 8192 * mib, UInt64.max] {
    #expect(throws: RuntimeFailure.self) {
      try WorkspaceMemoryBudget.guestBytes(
        requested: requested, hostPhysicalBytes: 8192 * mib,
        maximumVirtualMachineBytes: 8192 * mib)
    }
  }
}

@Test func automaticMemoryRoundTripsAsAbsentLimit() throws {
  let configuration = ServeConfiguration(
    id: "memory-test", controlSocketPath: "/tmp/memory-test.sock", rootfsPath: "/root.ext4",
    kernelPath: "/kernel", initfsPath: "/init.ext4", gatewayExecutablePath: "/gateway",
    cpus: 1, memoryBytes: nil, hostname: nil, mounts: [], initialArguments: nil)
  let decoded = try JSONDecoder().decode(
    ServeConfiguration.self, from: JSONEncoder().encode(configuration))
  try decoded.validate()
  #expect(decoded.memoryBytes == nil)
}

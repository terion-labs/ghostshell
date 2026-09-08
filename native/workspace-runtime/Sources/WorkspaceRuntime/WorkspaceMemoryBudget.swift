import Foundation

/// A finite guest ceiling, not a physical-memory reservation or ballooning policy.
/// Host and framework limits are inputs so boundary behavior needs no live VM.
enum WorkspaceMemoryBudget {
  static let mebibyte: UInt64 = 1024 * 1024
  static let minimumGuestBytes: UInt64 = 256 * mebibyte
  static let virtualMachineOverheadBytes: UInt64 = 128 * mebibyte

  static func guestBytes(
    requested: UInt64?, hostPhysicalBytes: UInt64, maximumVirtualMachineBytes: UInt64
  ) throws -> UInt64 {
    // Containerization adds this overhead and converts the guest limit to Int64.
    // Bound before adding or converting, and never rely on its silent host clamp.
    let maximum = min(hostPhysicalBytes, maximumVirtualMachineBytes, UInt64(Int64.max))
    let minimum = minimumGuestBytes + virtualMachineOverheadBytes
    guard maximum >= minimum else {
      throw RuntimeFailure("The host cannot provide the minimum workspace memory budget.")
    }
    if let requested {
      guard requested >= minimumGuestBytes,
        requested <= maximum - virtualMachineOverheadBytes
      else { throw RuntimeFailure("The requested workspace memory exceeds the supported budget.") }
      let alignedRequested = (requested + mebibyte - 1) / mebibyte * mebibyte
      guard alignedRequested <= maximum - virtualMachineOverheadBytes else {
        throw RuntimeFailure("The requested workspace memory exceeds the supported budget.")
      }
      return alignedRequested
    }

    // Each workspace may grow up to half host RAM, including SDK overhead.
    // This is not aggregate admission control: multiple VMs still compete.
    let budget = min(max(hostPhysicalBytes / 2, minimum), maximum)
    let alignedBudget = budget / mebibyte * mebibyte
    return alignedBudget - virtualMachineOverheadBytes
  }
}

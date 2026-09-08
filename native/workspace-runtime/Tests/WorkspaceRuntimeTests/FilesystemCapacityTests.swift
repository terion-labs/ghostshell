import Testing

@testable import WorkspaceRuntime

@Test func ordinaryWorkspaceCapacityRemains32GiB() throws {
  #expect(try RuntimeCommand.rootFilesystemCapacity(nil) == 32 * 1024 * 1024 * 1024)
}

@Test func serviceCapacityCanBe4GiB() throws {
  #expect(try RuntimeCommand.rootFilesystemCapacity("4096") == 4 * 1024 * 1024 * 1024)
}

@Test(arguments: ["", "0", "1023", "32769", "-4096", "+4096", "4.096", "4096 ", "９９９９", "18446744073709551615"])
func invalidFilesystemCapacityIsRejected(value: String) {
  #expect(throws: RuntimeFailure.self) {
    try RuntimeCommand.rootFilesystemCapacity(value)
  }
}

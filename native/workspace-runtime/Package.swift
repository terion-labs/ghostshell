// swift-tools-version: 6.2
import PackageDescription

let package = Package(
  name: "workspace-runtime",
  platforms: [.macOS(.v15)],
  products: [.executable(name: "workspace-runtime", targets: ["WorkspaceRuntime"])],
  dependencies: [.package(url: "https://github.com/apple/containerization.git", exact: "0.42.0")],
  targets: [
    .executableTarget(
      name: "WorkspaceRuntime",
      dependencies: [
        .product(name: "Containerization", package: "containerization"),
        .product(name: "ContainerizationExtras", package: "containerization"),
        .product(name: "ContainerizationOS", package: "containerization"),
        .product(name: "ContainerizationOCI", package: "containerization"),
      ]),
    .testTarget(name: "WorkspaceRuntimeTests", dependencies: ["WorkspaceRuntime"]),
  ]
)

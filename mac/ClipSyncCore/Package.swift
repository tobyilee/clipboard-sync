// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "ClipSyncCore",
    platforms: [.macOS(.v14)],
    products: [.library(name: "ClipSyncCore", targets: ["ClipSyncCore"])],
    targets: [
        .target(name: "ClipSyncCore"),
        .testTarget(name: "ClipSyncCoreTests", dependencies: ["ClipSyncCore"]),
    ]
)

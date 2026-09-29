// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "ClipSyncApp",
    platforms: [.macOS(.v14)],
    dependencies: [.package(path: "../ClipSyncCore")],
    targets: [
        .executableTarget(
            name: "ClipSync",
            dependencies: [.product(name: "ClipSyncCore", package: "ClipSyncCore")]
        ),
    ]
)

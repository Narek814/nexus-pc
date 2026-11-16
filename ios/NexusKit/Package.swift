// swift-tools-version:5.9
// NexusKit: protocol, pairing and crypto shared by the Nexus PC app, its widgets and Siri intents.
import PackageDescription

let package = Package(
    name: "NexusKit",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [
        .library(name: "NexusKit", targets: ["NexusKit"]),
    ],
    targets: [
        .target(name: "NexusKit"),
        .testTarget(name: "NexusKitTests", dependencies: ["NexusKit"]),
    ]
)

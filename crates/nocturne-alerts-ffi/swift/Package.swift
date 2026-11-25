// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "NocturneAlerts",
    platforms: [.iOS(.v15), .macOS(.v13)],
    products: [
        .library(name: "NocturneAlerts", targets: ["NocturneAlerts"]),
    ],
    targets: [
        .binaryTarget(
            name: "NocturneAlertsFFI",
            path: "NocturneAlertsFFI.xcframework"
        ),
        .target(
            name: "NocturneAlerts",
            dependencies: ["NocturneAlertsFFI"]
        ),
        .testTarget(
            name: "NocturneAlertsTests",
            dependencies: ["NocturneAlerts"]
        ),
    ]
)

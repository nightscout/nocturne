import Foundation
import XCTest
import NocturneAlerts

final class CorpusScenarioTests: XCTestCase {
    private static let scenario = "sustained-basic-window"

    private static var corpusDirectory: URL {
        var url = URL(fileURLWithPath: #filePath)
        for _ in 0..<6 { url.deleteLastPathComponent() }
        return url.appendingPathComponent("tests/Parity/AlertEngineCorpus")
    }

    private func load(_ name: String) throws -> [String: Any] {
        let data = try Data(contentsOf: Self.corpusDirectory.appendingPathComponent(name))
        return try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    private func decode(_ json: String) throws -> [String: Any] {
        try XCTUnwrap(try JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])
    }

    private func encode(_ object: [String: Any]) throws -> String {
        String(decoding: try JSONSerialization.data(withJSONObject: object), as: UTF8.self)
    }

    func testVersionIsReported() {
        XCTAssertFalse(NocturneAlerts.version().isEmpty)
    }

    func testScenarioReplaysToItsSnapshotThreadingTimersAndTracker() throws {
        let scenario = try load("\(Self.scenario).json")
        let expected = try load("\(Self.scenario).expected.json")
        let rules = try XCTUnwrap(scenario["rules"] as? [[String: Any]])
        let ticks = try XCTUnwrap(scenario["ticks"] as? [[String: Any]])
        let expectedTicks = try XCTUnwrap(expected["ticks"] as? [[String: Any]])
        XCTAssertEqual(rules.count, 1)
        XCTAssertEqual(ticks.count, expectedTicks.count)

        var timers: [String: Any] = [:]
        var notTrue: [String: Any] = [:]
        var tracker: Any = NSNull()
        var transitions: [String] = []

        for (tick, expectedTick) in zip(ticks, expectedTicks) {
            var request: [String: Any] = [
                "schema_version": 1,
                "rule": rules[0],
                "context": try XCTUnwrap(tick["context"]),
                "now": try XCTUnwrap(tick["at"]),
                "timers": timers,
                "not_true": notTrue,
            ]
            if !(tracker is NSNull) { request["tracker"] = tracker }

            let response = try decode(evaluate(requestJson: try encode(request)))
            XCTAssertEqual(response["ok"] as? Bool, true, "\(response)")

            let result = try XCTUnwrap(response["result"] as? NSDictionary)
            let expectedResult = try XCTUnwrap((expectedTick["rules"] as? [NSDictionary])?.first)
            // A snapshot pins `next_change_at` only by carrying it (the corpus README).
            let compared = NSMutableDictionary(dictionary: result)
            if expectedResult["next_change_at"] == nil { compared.removeObject(forKey: "next_change_at") }
            XCTAssertEqual(compared, expectedResult, "tick \(String(describing: tick["at"]))")

            transitions.append(try XCTUnwrap(result["transition"] as? String))
            timers = try XCTUnwrap(response["timers"] as? [String: Any])
            notTrue = try XCTUnwrap(response["not_true"] as? [String: Any])
            tracker = try XCTUnwrap(response["tracker"])
        }

        XCTAssertTrue(transitions.contains("opened"))
        XCTAssertTrue(transitions.contains("closed"))
    }
}
